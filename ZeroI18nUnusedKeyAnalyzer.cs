using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace ZeroI18n.SourceGeneration;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ZeroI18nUnusedKeyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ZERO005";

    private const string KeyAttributeName = "ZeroI18nKeyAttribute";
    private const string ReportProperty = "build_property.ZeroI18nReportUnusedKeys";
    private const string AllowListProperty = "build_property.ZeroI18nUnusedKeyAllowList";
    private const string AllowComment = "zeroi18n-allow-unused";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Unused i18n message",
        "The i18n message '{0}' is never used",
        "ZeroI18n",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A generated localization message has no reference in this compilation.",
        helpLinkUri: null,
        WellKnownDiagnosticTags.Unnecessary,
        WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!IsReportEnabled(start.Options))
            {
                return;
            }

            KeyMap? map = KeyMap.Create(start.Compilation);
            if (map is null)
            {
                return;
            }

            ConcurrentDictionary<string, byte> used = new(StringComparer.Ordinal);
            start.RegisterOperationAction(
                operationContext => CollectOperation(operationContext, map, used),
                OperationKind.Invocation,
                OperationKind.MethodReference,
                OperationKind.PropertyReference,
                OperationKind.NameOf);

            start.RegisterCompilationEndAction(end =>
            {
                CollectStringLiterals(end.Compilation, map, used, end.CancellationToken);
                CollectMarkup(end.Options.AdditionalFiles, map, used, end.CancellationToken);
                ReportUnused(end, map, used);
            });
        });
    }

    private static bool IsReportEnabled(AnalyzerOptions options)
    {
        if (!options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(ReportProperty, out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("0", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("off", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("disable", StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectOperation(OperationAnalysisContext context, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        switch (context.Operation)
        {
            case IInvocationOperation invocation:
                MarkMethod(invocation.TargetMethod, map, used);
                if (IsGeneratedGetString(invocation.TargetMethod))
                {
                    MarkArguments(invocation.Arguments, map, used);
                }

                break;
            case IMethodReferenceOperation methodReference:
                MarkMethod(methodReference.Method, map, used);
                break;
            case IPropertyReferenceOperation propertyReference when propertyReference.Property.IsIndexer && IsZeroI18nType(propertyReference.Property.ContainingType):
                MarkArguments(propertyReference.Arguments, map, used);
                break;
            case INameOfOperation nameOf:
                MarkNameOf(nameOf, context, map, used);
                break;
        }
    }

    private static void MarkNameOf(INameOfOperation nameOf, OperationAnalysisContext context, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        if (nameOf.Syntax is InvocationExpressionSyntax invocation)
        {
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                MarkSymbol(context.Operation.SemanticModel, argument.Expression, map, used, context.CancellationToken);
            }
        }

        MarkNameOf(nameOf.Argument, map, used);
    }

    private static void MarkSymbol(SemanticModel? model, SyntaxNode node, KeyMap map, ConcurrentDictionary<string, byte> used, CancellationToken cancellationToken)
    {
        if (model is null)
        {
            return;
        }

        SymbolInfo info = model.GetSymbolInfo(node, cancellationToken);
        if (info.Symbol is IMethodSymbol method)
        {
            MarkMethod(method, map, used);
        }

        foreach (ISymbol candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol candidateMethod)
            {
                MarkMethod(candidateMethod, map, used);
            }
        }
    }

    private static void MarkArguments(ImmutableArray<IArgumentOperation> arguments, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        foreach (IArgumentOperation argument in arguments)
        {
            if (argument.Value.ConstantValue.HasValue && argument.Value.ConstantValue.Value is string text)
            {
                MarkLookup(text, map, used);
            }
        }
    }

    private static void MarkNameOf(IOperation operation, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        if (operation is IMethodReferenceOperation methodReference)
        {
            MarkMethod(methodReference.Method, map, used);
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            MarkNameOf(child, map, used);
        }
    }

    private static void MarkMethod(IMethodSymbol method, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        if (map.Methods.TryGetValue(method.OriginalDefinition, out string? key) ||
            map.Methods.TryGetValue(method, out key))
        {
            used.TryAdd(key, 0);
        }
    }

    private static void MarkLookup(string text, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        if (map.Aliases.TryGetValue(text, out string? key) || map.MethodNames.TryGetValue(text, out key))
        {
            used.TryAdd(key, 0);
        }
    }

    private static bool IsGeneratedGetString(IMethodSymbol method)
    {
        return method.Name == "GetString" && IsZeroI18nType(method.ContainingType);
    }

    private static bool IsZeroI18nType(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "GeneratedCodeAttribute" &&
                attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string tool &&
                tool == "ZeroI18n")
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectStringLiterals(Compilation compilation, KeyMap map, ConcurrentDictionary<string, byte> used, CancellationToken cancellationToken)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsZeroI18nGeneratedTree(tree, cancellationToken))
            {
                continue;
            }

            SyntaxNode root = tree.GetRoot(cancellationToken);
            foreach (LiteralExpressionSyntax literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                MarkTokens(literal.Token.ValueText, map, used);
            }
        }
    }

    private static void CollectMarkup(ImmutableArray<AdditionalText> files, KeyMap map, ConcurrentDictionary<string, byte> used, CancellationToken cancellationToken)
    {
        if (files.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (AdditionalText file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsMarkup(file.Path))
            {
                continue;
            }

            SourceText? text = file.GetText(cancellationToken);
            if (text is null || text.Length == 0)
            {
                continue;
            }

            MarkTokens(text.ToString(), map, used);
        }
    }

    private static void MarkTokens(string text, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (map.Aliases.TryGetValue(text, out string? exact))
        {
            used.TryAdd(exact, 0);
            return;
        }

        foreach (KeyValuePair<string, string> alias in map.Aliases)
        {
            if (alias.Key.Length > 0 && alias.Key.Length <= text.Length && ContainsToken(text, alias.Key))
            {
                used.TryAdd(alias.Value, 0);
            }
        }
    }

    private static bool ContainsToken(string text, string token)
    {
        int index = 0;
        while (index < text.Length)
        {
            int found = text.IndexOf(token, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            int end = found + token.Length;
            bool startOk = found == 0 || !IsKeyCharacter(text[found - 1]);
            bool endOk = end >= text.Length || !IsKeyCharacter(text[end]);
            if (startOk && endOk)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }

    private static bool IsKeyCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.';
    }

    private static bool IsMarkup(string path)
    {
        return path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZeroI18nGeneratedTree(SyntaxTree tree, CancellationToken cancellationToken)
    {
        string path = tree.FilePath ?? string.Empty;
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
            path.IndexOf("ZeroI18n", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        SyntaxNode root = tree.GetRoot(cancellationToken);
        foreach (ClassDeclarationSyntax declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (declaration.Identifier.ValueText == KeyAttributeName)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReportUnused(CompilationAnalysisContext context, KeyMap map, ConcurrentDictionary<string, byte> used)
    {
        HashSet<string> allowList = ReadAllowList(context.Options);
        foreach (string key in map.Definitions.Keys.OrderBy(static item => item, StringComparer.Ordinal))
        {
            if (used.ContainsKey(key) || allowList.Contains(key))
            {
                continue;
            }

            KeyDefinition definition = map.Definitions[key];
            AdditionalText? file = FindFile(context.Options.AdditionalFiles, definition.Path);
            SourceText? text = file?.GetText(context.CancellationToken);
            int line = text is null ? definition.Line : ResolveLine(text, definition.Line, key);
            if (text is not null && IsAllowedByComment(text, line))
            {
                continue;
            }

            Location location = file is null || text is null
                ? Location.None
                : CreateLocation(file, text, line, key);
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, key));
        }
    }

    private static HashSet<string> ReadAllowList(AnalyzerOptions options)
    {
        HashSet<string> allowList = new(StringComparer.Ordinal);
        if (!options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(AllowListProperty, out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return allowList;
        }

        string[] parts = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string key = part.Trim();
            if (key.Length > 0)
            {
                allowList.Add(key);
            }
        }

        return allowList;
    }

    private static AdditionalText? FindFile(ImmutableArray<AdditionalText> files, string path)
    {
        if (files.IsDefaultOrEmpty || string.IsNullOrEmpty(path))
        {
            return null;
        }

        string normalized = NormalizePath(path);
        foreach (AdditionalText file in files)
        {
            if (string.Equals(NormalizePath(file.Path), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(PathAltSeparator, PathSeparator);
    }

    private const char PathAltSeparator = '/';
    private const char PathSeparator = '\\';

    private static int ResolveLine(SourceText text, int line, string key)
    {
        if (line > 0 && line <= text.Lines.Count)
        {
            return line;
        }

        for (int index = 0; index < text.Lines.Count; index++)
        {
            string content = text.Lines[index].ToString();
            if (ContainsToken(content, key))
            {
                return index + 1;
            }
        }

        return line;
    }

    private static bool IsAllowedByComment(SourceText text, int lineNumber)
    {
        if (lineNumber <= 1 || lineNumber > text.Lines.Count + 1)
        {
            return false;
        }

        for (int line = lineNumber - 2; line >= 0; line--)
        {
            string value = text.Lines[line].ToString().Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (value.StartsWith("#", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            {
                if (value.IndexOf(AllowComment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                continue;
            }

            return false;
        }

        return false;
    }

    private static Location CreateLocation(AdditionalText file, SourceText text, int lineNumber, string key)
    {
        if (lineNumber <= 0 || lineNumber > text.Lines.Count)
        {
            return Location.None;
        }

        TextLine line = text.Lines[lineNumber - 1];
        string content = line.ToString();
        TextSpan span = SpanOnLine(line, content, key);
        return Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span));
    }

    private static TextSpan SpanOnLine(TextLine line, string content, string key)
    {
        int column = content.IndexOf(key, StringComparison.Ordinal);
        if (column >= 0)
        {
            return new TextSpan(line.Start + column, key.Length);
        }

        string leaf = Leaf(key);
        column = content.IndexOf(leaf, StringComparison.Ordinal);
        if (column >= 0 && IsTokenAt(content, column, leaf.Length))
        {
            return new TextSpan(line.Start + column, leaf.Length);
        }

        return line.Span.Length > 0 ? line.Span : new TextSpan(line.Start, 0);
    }

    private static bool IsTokenAt(string text, int start, int length)
    {
        bool startOk = start == 0 || !IsKeyCharacter(text[start - 1]);
        int end = start + length;
        return startOk && (end >= text.Length || !IsKeyCharacter(text[end]));
    }

    private static string Leaf(string key)
    {
        int dot = key.LastIndexOf('.');
        int dash = key.LastIndexOf('-');
        int split = dot > dash ? dot : dash;
        return split >= 0 && split + 1 < key.Length ? key.Substring(split + 1) : key;
    }

    private sealed class KeyMap
    {
        private KeyMap(
            Dictionary<string, KeyDefinition> definitions,
            Dictionary<ISymbol, string> methods,
            Dictionary<string, string> aliases,
            Dictionary<string, string> methodNames)
        {
            Definitions = definitions;
            Methods = methods;
            Aliases = aliases;
            MethodNames = methodNames;
        }

        public Dictionary<string, KeyDefinition> Definitions { get; }

        public Dictionary<ISymbol, string> Methods { get; }

        public Dictionary<string, string> Aliases { get; }

        public Dictionary<string, string> MethodNames { get; }

        public static KeyMap? Create(Compilation compilation)
        {
            Dictionary<string, KeyDefinition> definitions = new(StringComparer.Ordinal);
            Dictionary<ISymbol, string> methods = new(SymbolEqualityComparer.Default);
            Dictionary<string, string> aliases = new(StringComparer.Ordinal);
            Dictionary<string, string> methodNames = new(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> dotted = new();

            foreach (IMethodSymbol method in EnumerateMethods(compilation.Assembly.GlobalNamespace))
            {
                foreach (AttributeData attribute in method.GetAttributes())
                {
                    if (attribute.AttributeClass?.Name != KeyAttributeName || attribute.ConstructorArguments.Length < 1)
                    {
                        continue;
                    }

                    if (attribute.ConstructorArguments[0].Value is not string key || key.Length == 0)
                    {
                        continue;
                    }

                    string path = attribute.ConstructorArguments.Length >= 2 && attribute.ConstructorArguments[1].Value is string pathValue
                        ? pathValue
                        : string.Empty;
                    int line = attribute.ConstructorArguments.Length >= 3 && attribute.ConstructorArguments[2].Value is int lineValue
                        ? lineValue
                        : 0;

                    if (!definitions.ContainsKey(key))
                    {
                        definitions.Add(key, new KeyDefinition(key, path, line));
                    }

                    if (!methods.ContainsKey(method))
                    {
                        methods.Add(method, key);
                    }

                    if (!methodNames.ContainsKey(method.Name))
                    {
                        methodNames.Add(method.Name, key);
                    }

                    if (!aliases.ContainsKey(key))
                    {
                        aliases.Add(key, key);
                    }

                    if (key.IndexOf('-') >= 0)
                    {
                        string dottedKey = key.Replace("-", ".");
                        dotted.Add(new KeyValuePair<string, string>(dottedKey, key));
                    }

                    break;
                }
            }

            foreach (KeyValuePair<string, string> alias in dotted)
            {
                if (!aliases.ContainsKey(alias.Key))
                {
                    aliases.Add(alias.Key, alias.Value);
                }
            }

            if (definitions.Count == 0)
            {
                return null;
            }

            return new KeyMap(definitions, methods, aliases, methodNames);
        }

        private static IEnumerable<IMethodSymbol> EnumerateMethods(INamespaceSymbol root)
        {
            Queue<INamespaceSymbol> namespaces = new();
            namespaces.Enqueue(root);
            while (namespaces.Count > 0)
            {
                INamespaceSymbol current = namespaces.Dequeue();
                foreach (INamespaceSymbol child in current.GetNamespaceMembers())
                {
                    namespaces.Enqueue(child);
                }

                foreach (INamedTypeSymbol type in current.GetTypeMembers())
                {
                    foreach (IMethodSymbol method in EnumerateTypeMethods(type))
                    {
                        yield return method;
                    }
                }
            }
        }

        private static IEnumerable<IMethodSymbol> EnumerateTypeMethods(INamedTypeSymbol type)
        {
            foreach (ISymbol member in type.GetMembers())
            {
                if (member is IMethodSymbol method)
                {
                    yield return method;
                }
                else if (member is INamedTypeSymbol nested)
                {
                    foreach (IMethodSymbol nestedMethod in EnumerateTypeMethods(nested))
                    {
                        yield return nestedMethod;
                    }
                }
            }
        }
    }

    private readonly struct KeyDefinition
    {
        public KeyDefinition(string key, string path, int line)
        {
            Key = key;
            Path = path;
            Line = line;
        }

        public string Key { get; }

        public string Path { get; }

        public int Line { get; }
    }
}
