using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ZeroI18n.SourceGeneration;
using Xunit;

namespace ZeroI18n.Tests;

public class FluentGeneratorTests
{
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

        public TestAnalyzerConfigOptionsProvider(AnalyzerConfigOptions? globalOptions = null)
        {
            _options = globalOptions ?? new TestAnalyzerConfigOptions();
        }

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            if (key == "build_property.RootNamespace")
            {
                value = "TestApp";
                return true;
            }
            if (key == "build_property.ZeroI18nClassName")
            {
                value = "Messages";
                return true;
            }
            if (key == "build_property.ZeroI18nDefaultLocale")
            {
                value = "en";
                return true;
            }
            value = null;
            return false;
        }
    }

    [Fact]
    public void Generator_Generates_Fluent_Messages_Correctly()
    {
        string enFtl = @"
# App Header
-brand = Toolkit
app-name = { -brand } App
welcome = Welcome, { $userName }!

# Plural selector
matched-records =
    { $count ->
        [one] Matched { $count } record
       *[other] Matched { $count } records
    }

# Attribute test
login-button = Login
    .tooltip = Click to sign in
";

        string zhFtl = @"
# 头部
-brand = Toolkit
app-name = { -brand } 应用程序
welcome = 欢迎，{ $userName }！

matched-records =
    { $count ->
       *[other] 匹配了 { $count } 条记录
    }

login-button = 登录
    .tooltip = 点击以登录
";

        string enPath = Path.Combine(Path.GetTempPath(), "messages", "en.ftl");
        string zhPath = Path.Combine(Path.GetTempPath(), "messages", "zh-CN.ftl");

        var enFile = new InMemoryAdditionalText(enPath, enFtl);
        var zhFile = new InMemoryAdditionalText(zhPath, zhFtl);

        var generator = new ZeroI18nGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator.AsSourceGenerator() },
            additionalTexts: new[] { enFile, zhFile },
            optionsProvider: new TestAnalyzerConfigOptionsProvider());

        var compilation = CSharpCompilation.Create("TestApp",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location),
                MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "System.Runtime").Location),
            });

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation outputCompilation, out ImmutableArray<Diagnostic> diagnostics);

        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var generatedSyntaxTrees = outputCompilation.SyntaxTrees.Skip(compilation.SyntaxTrees.Count()).ToList();
        Assert.Single(generatedSyntaxTrees);

        string generatedCode = generatedSyntaxTrees[0].ToString();
        Assert.Contains("public static string AppName()", generatedCode);
        Assert.Contains("public static string Welcome(object? userName)", generatedCode);
        Assert.Contains("public static string MatchedRecords(object? count)", generatedCode);
        Assert.Contains("public static string LoginButton()", generatedCode);
        Assert.Contains("public static string LoginButtonTooltip()", generatedCode);

        // Verify compilation into assembly
        using var ms = new MemoryStream();
        var emitResult = outputCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString())));

        var assembly = Assembly.Load(ms.ToArray());
        var messagesType = assembly.GetType("TestApp.I18n.Messages");
        Assert.NotNull(messagesType);

        // Test English
        var appNameMethod = messagesType.GetMethod("AppName", Type.EmptyTypes);
        Assert.NotNull(appNameMethod);
        Assert.Equal("Toolkit App", appNameMethod.Invoke(null, null));

        var welcomeMethod = messagesType.GetMethod("Welcome", new[] { typeof(object) });
        Assert.NotNull(welcomeMethod);
        Assert.Equal("Welcome, Alice!", welcomeMethod.Invoke(null, new object[] { "Alice" }));

        var matchedMethod = messagesType.GetMethod("MatchedRecords", new[] { typeof(object) });
        Assert.NotNull(matchedMethod);
        Assert.Equal("Matched 1 record", matchedMethod.Invoke(null, new object[] { 1 }));
        Assert.Equal("Matched 5 records", matchedMethod.Invoke(null, new object[] { 5 }));

        var loginTooltipMethod = messagesType.GetMethod("LoginButtonTooltip", Type.EmptyTypes);
        Assert.NotNull(loginTooltipMethod);
        Assert.Equal("Click to sign in", loginTooltipMethod.Invoke(null, null));

        // Test GetString with various keys
        var getStringMethod = messagesType.GetMethod("GetString", new[] { typeof(string) });
        Assert.NotNull(getStringMethod);
        Assert.Equal("Toolkit App", getStringMethod.Invoke(null, new object[] { "app-name" }));
        Assert.Equal("Toolkit App", getStringMethod.Invoke(null, new object[] { "app.name" }));
        Assert.Equal("Toolkit App", getStringMethod.Invoke(null, new object[] { "AppName" }));

        // Test Switch to Chinese
        var currentLocaleProp = messagesType.GetProperty("CurrentLocale");
        Assert.NotNull(currentLocaleProp);
        currentLocaleProp.SetValue(null, "zh-CN");

        Assert.Equal("Toolkit 应用程序", appNameMethod.Invoke(null, null));
        Assert.Equal("欢迎，Alice！", welcomeMethod.Invoke(null, new object[] { "Alice" }));
        Assert.Equal("匹配了 5 条记录", matchedMethod.Invoke(null, new object[] { 5 }));
        Assert.Equal("点击以登录", loginTooltipMethod.Invoke(null, null));
    }

    [Fact]
    public void Generator_Handles_Escapes_And_Multiline()
    {
        string enFtl = @"
escaped = Value with {""{\""quoted\""}""} and {""\""""}
multiline =
    First line
    Second line
";
        string enPath = Path.Combine(Path.GetTempPath(), "messages", "en.ftl");
        var enFile = new InMemoryAdditionalText(enPath, enFtl);

        var generator = new ZeroI18nGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator.AsSourceGenerator() },
            additionalTexts: new[] { enFile },
            optionsProvider: new TestAnalyzerConfigOptionsProvider());

        var compilation = CSharpCompilation.Create("TestApp",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location),
                MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "System.Runtime").Location),
            });

        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation outputCompilation, out ImmutableArray<Diagnostic> diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        using var ms = new MemoryStream();
        var emitResult = outputCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString())));

        var assembly = Assembly.Load(ms.ToArray());
        var messagesType = assembly.GetType("TestApp.I18n.Messages");
        Assert.NotNull(messagesType);

        var escapedMethod = messagesType.GetMethod("Escaped", Type.EmptyTypes);
        Assert.NotNull(escapedMethod);
        Assert.Equal("Value with {\"quoted\"} and \"", escapedMethod.Invoke(null, null));

        var multilineMethod = messagesType.GetMethod("Multiline", Type.EmptyTypes);
        Assert.NotNull(multilineMethod);
        Assert.Equal("First line\nSecond line", multilineMethod.Invoke(null, null));
    }

    [Fact]
    public void Generator_Reports_Invalid_Fluent_Diagnostic()
    {
        string invalidFtl = @"
valid-entry = Good
not an entry without equals
";
        string path = Path.Combine(Path.GetTempPath(), "messages", "en.ftl");
        var file = new InMemoryAdditionalText(path, invalidFtl);

        var generator = new ZeroI18nGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator.AsSourceGenerator() },
            additionalTexts: new[] { file },
            optionsProvider: new TestAnalyzerConfigOptionsProvider());

        var compilation = CSharpCompilation.Create("TestApp",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out ImmutableArray<Diagnostic> diagnostics);

        var error = Assert.Single(diagnostics.Where(d => d.Id == "ZERO004"));
        Assert.Contains("expected '='", error.GetMessage());
    }
}
