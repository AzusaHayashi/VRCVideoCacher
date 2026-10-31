using System.Reflection;
using Xunit;

namespace CookieBridge.TestRunner;

/// <summary>
/// In-process executor for the xUnit test assembly.
///
/// Why this exists: `dotnet test` (vstest) cannot start a single test in this environment — the vstest
/// testhost dies during startup inside
/// <c>Microsoft.VisualStudio.TestPlatform.TestHost.DefaultEngineInvoker.SetParentProcessExitCallback</c>
/// with <c>Win32Exception (5) Access is denied</c> when it calls <c>Process.EnableRaisingEvents</c> on its
/// parent process handle. That is before any test code runs, and it is not fixable from the test project.
///
/// The test project keeps the canonical shape (`xunit` + `xunit.runner.visualstudio` +
/// `Microsoft.NET.Test.Sdk`, so `dotnet test` works on a normal machine). This harness runs the very same
/// compiled test assembly, honouring:
///   * a fresh instance per test (xunit semantics) and <see cref="IDisposable"/>/<see cref="IAsyncLifetime"/> dispose,
///   * <c>[Fact]</c>, <c>[Theory]</c> + <c>[InlineData]</c>, <c>[Skip]</c>,
///   * serial execution (xunit.runner.json sets parallelizeTestCollections=false),
///   * collection fixtures (<c>ICollectionFixture&lt;T&gt;</c>) and class fixtures.
/// A test fails when it throws — exactly what an xUnit assertion does.
///
/// Usage: CookieBridge.TestRunner.exe &lt;test.dll&gt; [--filter substring] [--list]
/// Exit code 0 = every test passed, 1 = failures, 2 = usage error.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: CookieBridge.TestRunner <test-assembly.dll> [--filter substring] [--list]");
            return 2;
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"test assembly not found: {assemblyPath}");
            return 2;
        }

        string? filter = null;
        var listOnly = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--list") listOnly = true;
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var cases = Discover(assembly).ToList();

        if (filter != null)
            cases = cases.Where(c => c.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (listOnly)
        {
            foreach (var c in cases) Console.WriteLine(c.DisplayName);
            return 0;
        }

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var group in cases.GroupBy(c => c.TestClass))
        {
            var collectionFixtures = BuildCollectionFixtures(group.Key);
            try
            {
                foreach (var testCase in group)
                {
                    if (testCase.SkipReason != null)
                    {
                        skipped++;
                        Console.WriteLine($"  SKIP  {testCase.DisplayName} ({testCase.SkipReason})");
                        continue;
                    }

                    object? instance = null;
                    try
                    {
                        instance = Activator.CreateInstance(testCase.TestClass);
                        InjectCollectionFixtures(testCase.TestClass, instance, collectionFixtures);
                        testCase.Invoke(instance);
                        passed++;
                        Console.WriteLine($"  PASS  {testCase.DisplayName}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        var actual = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
                        Console.WriteLine($"  FAIL  {testCase.DisplayName}");
                        Console.WriteLine($"        {actual.GetType().Name}: {Flatten(actual.Message)}");
                        var frame = FirstUserFrame(actual);
                        if (frame != null) Console.WriteLine($"        at {frame}");
                        failures.Add($"{testCase.DisplayName} :: {actual.GetType().Name}: {Flatten(actual.Message)}");
                    }
                    finally
                    {
                        DisposeInstance(instance);
                    }
                }
            }
            finally
            {
                foreach (var fixture in collectionFixtures.Values.OfType<IDisposable>().Distinct())
                    SafeDispose(fixture);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"TOTAL {passed + failed + skipped}  PASSED {passed}  FAILED {failed}  SKIPPED {skipped}");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("FAILURES:");
            foreach (var failure in failures) Console.WriteLine($"  - {failure}");
        }

        return failed == 0 ? 0 : 1;
    }

    // ---- discovery -------------------------------------------------------------

    private sealed record TestCase(
        Type TestClass,
        string DisplayName,
        object?[] Arguments,
        string? SkipReason,
        Action<object?> Invoke);

    private static IEnumerable<TestCase> Discover(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var isFact = method.GetCustomAttribute<FactAttribute>() != null;
                var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToArray();

                if (!isFact && inlineData.Length == 0) continue;

                var skipReason = method.GetCustomAttribute<FactAttribute>()?.Skip;

                if (inlineData.Length > 0)
                {
                    foreach (var data in inlineData)
                    {
                        var arguments = FitArguments(data.GetData(method).FirstOrDefault() ?? [], method);
                        var display = $"{type.Name}.{method.Name}({FormatArguments(arguments)})";
                        yield return new TestCase(type, display, arguments, skipReason,
                            instance => InvokeMethod(method, instance, arguments));
                    }

                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length > 0) continue;   // no data source support needed here

                yield return new TestCase(type, $"{type.Name}.{method.Name}()", [], skipReason,
                    instance => InvokeMethod(method, instance, []));
            }
        }
    }

    /// <summary>[InlineData(null)] arrives as an empty row; pad it so the null argument is actually passed.</summary>
    private static object?[] FitArguments(object?[] row, MethodInfo method)
    {
        var expected = method.GetParameters().Length;
        if (row.Length == expected) return row;

        var fitted = new object?[expected];
        for (var i = 0; i < expected; i++) fitted[i] = i < row.Length ? row[i] : null;
        return fitted;
    }

    private static void InvokeMethod(MethodInfo method, object? instance, object?[] arguments)
    {
        var result = method.Invoke(instance, arguments);
        if (result is Task task) task.GetAwaiter().GetResult();
        else if (result is ValueTask valueTask) valueTask.AsTask().GetAwaiter().GetResult();
    }

    private static string FormatArguments(object?[] arguments) =>
        string.Join(", ", arguments.Select(a => a switch
        {
            null => "null",
            string s => $"\"{s}\"",
            _ => a.ToString(),
        }));

    // ---- fixtures --------------------------------------------------------------

    private static Dictionary<Type, object> BuildCollectionFixtures(Type testClass)
    {
        var fixtures = new Dictionary<Type, object>();

        var definition = testClass.GetCustomAttributes<CollectionAttribute>().FirstOrDefault();
        if (definition == null) return fixtures;

        var collectionName = CollectionAttributeName.Read(definition);

        var definitionType = testClass.Assembly.GetTypes()
            .FirstOrDefault(t => CollectionAttributeName.ReadDefinition(t.GetCustomAttribute<CollectionDefinitionAttribute>()) == collectionName);

        if (definitionType == null) return fixtures;

        foreach (var contract in definitionType.GetInterfaces().Where(i => i.IsGenericType &&
                     i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)))
        {
            var fixtureType = contract.GetGenericArguments()[0];
            fixtures[fixtureType] = Activator.CreateInstance(fixtureType)!;
        }

        return fixtures;
    }

    private static void InjectCollectionFixtures(Type testClass, object? instance, Dictionary<Type, object> fixtures)
    {
        if (instance == null || fixtures.Count == 0) return;

        foreach (var property in testClass.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (fixtures.TryGetValue(property.PropertyType, out var fixture) && property.CanWrite)
                property.SetValue(instance, fixture);

        foreach (var field in testClass.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (fixtures.TryGetValue(field.FieldType, out var fixture))
                field.SetValue(instance, fixture);
    }

    private static void DisposeInstance(object? instance)
    {
        try
        {
            if (instance is IAsyncLifetime asyncLifetime) ((Task)asyncLifetime.DisposeAsync()).GetAwaiter().GetResult();
            else if (instance is IDisposable disposable) disposable.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        (dispose threw {ex.GetType().Name}: {Flatten(ex.Message)})");
        }
    }

    private static void SafeDispose(object instance)
    {
        try
        {
            if (instance is IDisposable disposable) disposable.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    // ---- reporting helpers -----------------------------------------------------

    /// <summary>[Collection]/[CollectionDefinition] expose their name through a private field in xunit 2.x.</summary>
    private static class CollectionAttributeName
    {
        public static string? Read(CollectionAttribute? attribute) => ReadField(attribute);

        public static string? ReadDefinition(CollectionDefinitionAttribute? attribute) => ReadField(attribute);

        private static string? ReadField(object? attribute)
        {
            if (attribute == null) return null;
            var type = attribute.GetType();
            while (type != null)
            {
                var field = type.GetField("name", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? type.GetField("Name", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (field?.GetValue(attribute) is string value) return value;
                type = type.BaseType;
            }

            return null;
        }
    }

    private static string Flatten(string message) => message.Replace("\r", string.Empty).Replace("\n", " | ");

    private static string? FirstUserFrame(Exception ex)
    {
        var frames = (ex.StackTrace ?? string.Empty).Split('\n');
        foreach (var frame in frames)
        {
            var trimmed = frame.Trim();
            if (!trimmed.Contains("CookieBridge.Tests", StringComparison.Ordinal)) continue;
            var end = trimmed.IndexOf(" in ", StringComparison.Ordinal);
            return end > 0 ? trimmed[..end] : trimmed;
        }

        return null;
    }
}
