using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ZeroI18n.SourceGeneration;
using Xunit;

namespace ZeroI18n.Tests;

public class UnusedKeyAnalyzerTests
{
    [Fact]
    public async Task Reports_unused_key_on_the_default_locale_file()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            internal static class Demo
            {
                public static string Show() => TestApp.I18n.Messages.Used();
            }
            """,
            File("messages/en.ftl", "used = Used\nspare = Spare\n"),
            File("messages/zh-CN.ftl", "used = 已用\nspare = 未用\n"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("spare", MessageKey(diagnostic));
        Assert.Contains("en.ftl", diagnostic.Location.GetLineSpan().Path);
        Assert.DoesNotContain("zh-CN.ftl", diagnostic.Location.GetLineSpan().Path);
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task Counts_method_locale_lookup_nameof_and_indexer()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            internal static class Demo
            {
                public static string A() => TestApp.I18n.Messages.Called();
                public static string B() => TestApp.I18n.Messages.LocaleForLocale("en");
                public static string C() => TestApp.I18n.Messages.GetString("app.name");
                public static string D() => TestApp.I18n.Messages.GetString("Method");
                public static string E() => TestApp.I18n.LocalizationManager.Instance["Index"];
                public static string F() => nameof(TestApp.I18n.Messages.Named);
            }
            """,
            File("messages/en.ftl", """
                called = Called
                locale = Locale
                app-name = App
                method = Method
                index = Index
                named = Named
                spare = Spare
                """));

        Assert.Equal("spare", MessageKey(Assert.Single(diagnostics)));
    }

    [Fact]
    public async Task Counts_string_tokens_and_markup_files()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            internal static class Demo
            {
                public const string Id = "literal";
            }
            """,
            File("messages/en.ftl", "literal = Literal\nbound = Bound\nspare = Spare\n"),
            File("views/MainWindow.axaml", "<TextBlock Text=\"{Binding [bound]}\" />"));

        Assert.Equal("spare", MessageKey(Assert.Single(diagnostics)));
    }

    [Fact]
    public async Task Honors_allow_comment_allow_list_and_switch()
    {
        string source = """
            internal static class Demo
            {
                public static string Show() => TestApp.I18n.Messages.Login();
            }
            """;
        (string Path, string Content) catalog = File("messages/en.ftl", """
            # zeroi18n-allow-unused
            kept = Kept

            login = Login
            # zeroi18n-allow-unused
                .tooltip = Tip

            spare = Spare
            """);

        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync(source, catalog));
        Assert.Equal("spare", MessageKey(diagnostic));

        Assert.Empty(await AnalyzeAsync(source, new Dictionary<string, string>
        {
            ["build_property.ZeroI18nUnusedKeyAllowList"] = "spare; missing"
        }, catalog));

        Assert.Empty(await AnalyzeAsync(source, new Dictionary<string, string>
        {
            ["build_property.ZeroI18nReportUnusedKeys"] = "false"
        }, catalog));
    }

    [Fact]
    public async Task Dynamic_lookup_leaves_other_keys_unused()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            internal static class Demo
            {
                public static string All(string key) => TestApp.I18n.Messages.GetString(key);
                public static string One() => TestApp.I18n.Messages.Used();
            }
            """,
            File("messages/en.ftl", "used = Used\nspare = Spare\n"));

        Assert.Equal("spare", MessageKey(Assert.Single(diagnostics)));
    }

    [Fact]
    public async Task Reports_json_key_on_its_property_line()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            """
            internal static class Demo
            {
                public static string Show() => TestApp.I18n.Messages.Used();
            }
            """,
            File("messages/en.json", "{\n  \"used\": \"Used\",\n  \"spare\": \"Spare\"\n}\n"));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("spare", MessageKey(diagnostic));
        Assert.Equal(2, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains("en.json", diagnostic.Location.GetLineSpan().Path);
    }

    private static string MessageKey(Diagnostic diagnostic)
    {
        Assert.Equal(ZeroI18nUnusedKeyAnalyzer.DiagnosticId, diagnostic.Id);
        return diagnostic.GetMessage().Replace("The i18n message '", string.Empty).Replace("' is never used", string.Empty);
    }

    private static (string Path, string Content) File(string relativePath, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "zeroi18n-unused", relativePath.Replace('/', Path.DirectorySeparatorChar));
        return (path, content);
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string userCode,
        params (string Path, string Content)[] files)
    {
        return AnalyzeAsync(userCode, new Dictionary<string, string>(), files);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string userCode,
        Dictionary<string, string> properties,
        params (string Path, string Content)[] files)
    {
        Dictionary<string, string> options = new(properties)
        {
            ["build_property.RootNamespace"] = "TestApp",
            ["build_property.ZeroI18nClassName"] = "Messages",
            ["build_property.ZeroI18nDefaultLocale"] = "en"
        };
        TestAnalyzerConfigOptionsProvider provider = new(options);
        ImmutableArray<AdditionalText> additional = files
            .Select(file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Content))
            .ToImmutableArray();

        Compilation compilation = CSharpCompilation.Create(
            "TestApp",
            new[] { CSharpSyntaxTree.ParseText(userCode) },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new ZeroI18nGenerator().AsSourceGenerator() },
            additionalTexts: additional,
            optionsProvider: provider);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.Empty(generatorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        ImmutableArray<Diagnostic> compileErrors = output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        Assert.True(compileErrors.IsEmpty, string.Join("\n", compileErrors));

        CompilationWithAnalyzers analyzed = output.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ZeroI18nUnusedKeyAnalyzer()),
            new AnalyzerOptions(additional, provider));
        ImmutableArray<Diagnostic> diagnostics = await analyzed.GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(diagnostic => diagnostic.Id == ZeroI18nUnusedKeyAnalyzer.DiagnosticId).ToImmutableArray();
    }

    private static IEnumerable<MetadataReference> References()
    {
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location);
        Assembly? runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "System.Runtime");
        if (runtime is not null)
        {
            yield return MetadataReference.CreateFromFile(runtime.Location);
        }
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _options;

        public TestAnalyzerConfigOptionsProvider(Dictionary<string, string> values)
        {
            _options = new MapOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class MapOptions : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;

        public MapOptions(Dictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            return _values.TryGetValue(key, out value);
        }
    }
}
