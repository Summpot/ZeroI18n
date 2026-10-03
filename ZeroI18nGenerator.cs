using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ZeroI18n.SourceGeneration;

[Generator(LanguageNames.CSharp)]
public sealed class ZeroI18nGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidJson = new(
        "ZERO001",
        "Invalid i18n JSON",
        "Could not parse '{0}': {1}",
        "ZeroI18n",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedValue = new(
        "ZERO002",
        "Unsupported i18n value",
        "The value for '{0}' in '{1}' is not a supported localized string",
        "ZeroI18n",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateMessage = new(
        "ZERO003",
        "Duplicate i18n message",
        "The message '{0}' for locale '{1}' appears more than once; '{2}' wins",
        "ZeroI18n",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidFluent = new(
        "ZERO004",
        "Invalid i18n Fluent",
        "Could not parse '{0}': {1}",
        "ZeroI18n",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<GeneratorOptions> optionsProvider =
            context.AnalyzerConfigOptionsProvider.Select(static (options, _) => GeneratorOptions.From(options.GlobalOptions));

        IncrementalValuesProvider<I18nFile> filesProvider = context.AdditionalTextsProvider
            .Where(static file => IsCandidateFile(file.Path))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (source, cancellationToken) =>
                I18nFile.Read(source.Left, source.Right.GetOptions(source.Left), cancellationToken));

        context.RegisterSourceOutput(
            optionsProvider.Combine(filesProvider.Collect()),
            static (context, source) => Execute(context, source.Left, source.Right));
    }

    private static bool IsCandidateFile(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".ftl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string separator = Path.DirectorySeparatorChar.ToString();
        return normalized.IndexOf(separator + "messages" + separator, StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(separator + "locales" + separator, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void Execute(SourceProductionContext context, GeneratorOptions options, ImmutableArray<I18nFile> files)
    {
        if (files.IsDefaultOrEmpty)
        {
            return;
        }

        List<RawMessage> rawMessages = new();
        List<Diagnostic> diagnostics = new();

        foreach (I18nFile file in files.OrderBy(static file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.AddRange(file.Diagnostics);
            rawMessages.AddRange(file.Messages);
        }

        foreach (Diagnostic diagnostic in diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        if (rawMessages.Count == 0)
        {
            return;
        }

        GenerationModel model = GenerationModel.Create(rawMessages, options, diagnostics);
        foreach (Diagnostic diagnostic in model.Diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        SourceText sourceText = SourceText.From(SourceEmitter.Emit(model), Encoding.UTF8);
        context.AddSource(options.ClassName + ".g.cs", sourceText);
    }

    private sealed class GeneratorOptions
    {
        private GeneratorOptions(string @namespace, string className, string accessModifier, string? defaultLocale)
        {
            Namespace = @namespace;
            ClassName = className;
            AccessModifier = accessModifier;
            DefaultLocale = defaultLocale;
        }

        public string Namespace { get; }

        public string ClassName { get; }

        public string AccessModifier { get; }

        public string? DefaultLocale { get; }

        public static GeneratorOptions From(AnalyzerConfigOptions options)
        {
            string rootNamespace = TryGet(options, "build_property.RootNamespace");
            string configuredNamespace = TryGet(options, "build_property.ZeroI18nNamespace");
            string configuredClassName = TryGet(options, "build_property.ZeroI18nClassName");
            string configuredAccess = TryGet(options, "build_property.ZeroI18nAccessModifier");
            string configuredDefaultLocale = TryGet(options, "build_property.ZeroI18nDefaultLocale");

            string @namespace = IsValidNamespace(configuredNamespace)
                ? configuredNamespace
                : (IsValidNamespace(rootNamespace) ? rootNamespace + ".I18n" : "ZeroI18n");

            string className = Identifier.ToPascal(configuredClassName);
            if (string.IsNullOrWhiteSpace(className))
            {
                className = "Messages";
            }

            string accessModifier = configuredAccess.Equals("public", StringComparison.OrdinalIgnoreCase)
                ? "public"
                : "internal";

            return new GeneratorOptions(@namespace, className, accessModifier, string.IsNullOrWhiteSpace(configuredDefaultLocale) ? null : configuredDefaultLocale);
        }

        private static string TryGet(AnalyzerConfigOptions options, string key)
        {
            return options.TryGetValue(key, out string? value) ? value?.Trim() ?? string.Empty : string.Empty;
        }

        private static bool IsValidNamespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] parts = value.Split('.');
            foreach (string part in parts)
            {
                if (!Identifier.IsValidIdentifier(part))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class I18nFile
    {
        private I18nFile(string path, IReadOnlyList<RawMessage> messages, IReadOnlyList<Diagnostic> diagnostics)
        {
            Path = path;
            Messages = messages;
            Diagnostics = diagnostics;
        }

        public string Path { get; }

        public IReadOnlyList<RawMessage> Messages { get; }

        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public static I18nFile Read(AdditionalText additionalText, AnalyzerConfigOptions metadata, CancellationToken cancellationToken)
        {
            string path = additionalText.Path;
            List<RawMessage> messages = new();
            List<Diagnostic> diagnostics = new();

            LocaleFileInfo? fileInfo = LocaleFileInfo.From(path, metadata);
            if (fileInfo is null)
            {
                return new I18nFile(path, messages, diagnostics);
            }

            SourceText? text = additionalText.GetText(cancellationToken);
            if (text is null)
            {
                var desc = fileInfo.Format == MessageFormat.Fluent ? InvalidFluent : InvalidJson;
                diagnostics.Add(Diagnostic.Create(desc, Location.None, path, "file is empty or unreadable"));
                return new I18nFile(path, messages, diagnostics);
            }

            if (fileInfo.Format == MessageFormat.Fluent)
            {
                try
                {
                    FluentParser.Parse(text.ToString(), fileInfo.KeyPrefix, fileInfo, path, messages, diagnostics);
                }
                catch (FluentParseException exception)
                {
                    diagnostics.Add(Diagnostic.Create(InvalidFluent, Location.None, path, exception.Message));
                }

                return new I18nFile(path, messages, diagnostics);
            }

            try
            {
                JsonValue root = JsonParser.Parse(text.ToString());
                if (root is JsonObject rootObject)
                {
                    CollectMessages(rootObject, fileInfo.KeyPrefix, fileInfo, path, messages, diagnostics);
                }
                else
                {
                    diagnostics.Add(Diagnostic.Create(InvalidJson, Location.None, path, "root value must be a JSON object"));
                }
            }
            catch (JsonParseException exception)
            {
                diagnostics.Add(Diagnostic.Create(InvalidJson, Location.None, path, exception.Message));
            }

            return new I18nFile(path, messages, diagnostics);
        }

        private static void CollectMessages(
            JsonObject jsonObject,
            string keyPrefix,
            LocaleFileInfo fileInfo,
            string path,
            List<RawMessage> messages,
            List<Diagnostic> diagnostics)
        {
            foreach (JsonProperty property in jsonObject.Properties)
            {
                if (property.Name == "$schema")
                {
                    continue;
                }

                string key = string.IsNullOrEmpty(keyPrefix) ? property.Name : keyPrefix + "." + property.Name;
                CollectValue(property.Value, key, fileInfo, path, messages, diagnostics, property.Line);
            }
        }

        private static void CollectValue(
            JsonValue value,
            string key,
            LocaleFileInfo fileInfo,
            string path,
            List<RawMessage> messages,
            List<Diagnostic> diagnostics,
            int line)
        {
            if (value is JsonString text)
            {
                messages.Add(RawMessage.Simple(key, fileInfo.Locale, fileInfo.Format, Template.Parse(text.Value, fileInfo.Format), path, line));
                return;
            }

            if (value is JsonObject nested)
            {
                CollectMessages(nested, key, fileInfo, path, messages, diagnostics);
                return;
            }

            if (value is JsonArray array && fileInfo.Format == MessageFormat.MessageFormat)
            {
                if (TryCollectMessageFormatVariants(array, key, fileInfo, path, messages, line))
                {
                    return;
                }
            }

            diagnostics.Add(Diagnostic.Create(UnsupportedValue, Location.None, key, path));
        }

        private static bool TryCollectMessageFormatVariants(
            JsonArray array,
            string key,
            LocaleFileInfo fileInfo,
            string path,
            List<RawMessage> messages,
            int line)
        {
            if (array.Items.Count != 1 || array.Items[0] is not JsonObject messageObject)
            {
                return false;
            }

            JsonValue? matchValue = messageObject.TryGet("match");
            if (matchValue is not JsonObject matchObject)
            {
                return false;
            }

            string? selectorVariable = FindSelectorVariable(messageObject);
            bool foundVariant = false;

            foreach (JsonProperty property in matchObject.Properties)
            {
                if (property.Value is not JsonString text)
                {
                    continue;
                }

                string? category = ExtractMessageFormatCategory(property.Name);
                if (string.IsNullOrEmpty(category))
                {
                    continue;
                }

                Template template = Template.Parse(text.Value, fileInfo.Format);
                messages.Add(RawMessage.Plural(key, fileInfo.Locale, fileInfo.Format, category!, false, template, path, selectorVariable, line));
                foundVariant = true;
            }

            return foundVariant;
        }

        private static string? FindSelectorVariable(JsonObject messageObject)
        {
            JsonValue? declarationsValue = messageObject.TryGet("declarations");
            if (declarationsValue is not JsonArray declarations)
            {
                return null;
            }

            foreach (JsonValue declarationValue in declarations.Items)
            {
                if (declarationValue is not JsonString declaration)
                {
                    continue;
                }

                string value = declaration.Value.Trim();
                if (value.StartsWith("input ", StringComparison.Ordinal))
                {
                    string candidate = value.Substring("input ".Length).Trim();
                    if (Identifier.IsValidMessageVariable(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static string? ExtractMessageFormatCategory(string selector)
        {
            int index = selector.LastIndexOf('=');
            string category = index >= 0 ? selector.Substring(index + 1) : selector;
            category = category.Trim();
            return IsPluralCategory(category) ? category : null;
        }
    }

    private sealed class LocaleFileInfo
    {
        private LocaleFileInfo(string locale, MessageFormat format, string keyPrefix)
        {
            Locale = locale;
            Format = format;
            KeyPrefix = keyPrefix;
        }

        public string Locale { get; }

        public MessageFormat Format { get; }

        public string KeyPrefix { get; }

        public static LocaleFileInfo? From(string path, AnalyzerConfigOptions metadata)
        {
            MessageFormat format = ResolveFormat(path, metadata);
            string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] parts = normalized.Split(Path.DirectorySeparatorChar);
            string directoryName = format == MessageFormat.I18Next ? "locales" : (format == MessageFormat.MessageFormat ? "messages" : (normalized.IndexOf(Path.DirectorySeparatorChar + "locales" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ? "locales" : "messages"));
            int directoryIndex = LastIndexOf(parts, directoryName);
            if (directoryIndex < 0 || directoryIndex == parts.Length - 1)
            {
                return null;
            }

            string[] relative = parts.Skip(directoryIndex + 1).ToArray();
            string fileName = Path.GetFileNameWithoutExtension(relative[relative.Length - 1]);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            string locale;
            string keyPrefix = string.Empty;

            if (format == MessageFormat.I18Next && relative.Length > 1)
            {
                locale = relative[0];
                keyPrefix = BuildKeyPrefix(relative.Skip(1).ToArray());
            }
            else
            {
                locale = fileName;
                if (relative.Length > 1)
                {
                    keyPrefix = BuildKeyPrefix(relative.Take(relative.Length - 1).ToArray());
                }
            }

            return new LocaleFileInfo(locale, format, keyPrefix);
        }

        private static MessageFormat ResolveFormat(string path, AnalyzerConfigOptions metadata)
        {
            if (metadata.TryGetValue("build_metadata.AdditionalFiles.ZeroI18nFormat", out string? format))
            {
                if (format.Equals("fluent", StringComparison.OrdinalIgnoreCase))
                {
                    return MessageFormat.Fluent;
                }

                if (format.Equals("i18next", StringComparison.OrdinalIgnoreCase))
                {
                    return MessageFormat.I18Next;
                }

                if (format.Equals("messageFormat", StringComparison.OrdinalIgnoreCase))
                {
                    return MessageFormat.MessageFormat;
                }
            }

            if (path.EndsWith(".ftl", StringComparison.OrdinalIgnoreCase))
            {
                return MessageFormat.Fluent;
            }

            string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string separator = Path.DirectorySeparatorChar.ToString();
            return normalized.IndexOf(separator + "locales" + separator, StringComparison.OrdinalIgnoreCase) >= 0
                ? MessageFormat.I18Next
                : MessageFormat.MessageFormat;
        }

        private static int LastIndexOf(string[] parts, string value)
        {
            for (int index = parts.Length - 1; index >= 0; index--)
            {
                if (parts[index].Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static string BuildKeyPrefix(string[] pathParts)
        {
            List<string> keyParts = new();
            foreach (string part in pathParts)
            {
                string value = Path.GetFileNameWithoutExtension(part);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    keyParts.Add(value);
                }
            }

            return string.Join(".", keyParts);
        }
    }

    private enum MessageFormat
    {
        MessageFormat,
        I18Next,
        Fluent
    }

    private sealed class RawMessage
    {
        private RawMessage(
            string key,
            string locale,
            MessageFormat format,
            Template template,
            string path,
            string? pluralCategory,
            bool isOrdinal,
            string? selectorVariable,
            int line)
        {
            Key = key;
            Locale = locale;
            Format = format;
            Template = template;
            Path = path;
            PluralCategory = pluralCategory;
            IsOrdinal = isOrdinal;
            SelectorVariable = selectorVariable;
            Line = line;
        }

        public string Key { get; }

        public string Locale { get; }

        public MessageFormat Format { get; }

        public Template Template { get; }

        public string Path { get; }

        public string? PluralCategory { get; }

        public bool IsOrdinal { get; }

        public string? SelectorVariable { get; }

        public int Line { get; }

        public static RawMessage Simple(string key, string locale, MessageFormat format, Template template, string path, int line = 0)
        {
            return new RawMessage(key, locale, format, template, path, null, false, null, line);
        }

        public static RawMessage Plural(string key, string locale, MessageFormat format, string category, bool isOrdinal, Template template, string path, string? selectorVariable = null, int line = 0)
        {
            return new RawMessage(key, locale, format, template, path, category, isOrdinal, selectorVariable, line);
        }

        public static void SelectDefinition(IReadOnlyList<RawMessage> messages, string defaultLocale, out string path, out int line)
        {
            RawMessage? selected = null;
            foreach (RawMessage message in messages)
            {
                if (string.Equals(message.Locale, defaultLocale, StringComparison.Ordinal))
                {
                    selected = message;
                    break;
                }

                selected ??= message;
            }

            path = selected?.Path ?? string.Empty;
            line = selected?.Line ?? 0;
        }
    }

    private sealed class GenerationModel
    {
        private GenerationModel(
            GeneratorOptions options,
            IReadOnlyList<string> locales,
            string defaultLocale,
            IReadOnlyList<MessageMethod> methods,
            IReadOnlyList<PluralMethod> pluralMethods,
            IReadOnlyList<Diagnostic> diagnostics)
        {
            Options = options;
            Locales = locales;
            DefaultLocale = defaultLocale;
            Methods = methods;
            PluralMethods = pluralMethods;
            Diagnostics = diagnostics;
        }

        public GeneratorOptions Options { get; }

        public IReadOnlyList<string> Locales { get; }

        public string DefaultLocale { get; }

        public IReadOnlyList<MessageMethod> Methods { get; }

        public IReadOnlyList<PluralMethod> PluralMethods { get; }

        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public static GenerationModel Create(IReadOnlyList<RawMessage> rawMessages, GeneratorOptions options, List<Diagnostic> diagnostics)
        {
            List<string> locales = rawMessages
                .Select(static message => message.Locale)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static locale => locale, StringComparer.Ordinal)
                .ToList();

            string defaultLocale = ResolveDefaultLocale(locales, options.DefaultLocale);
            List<Diagnostic> modelDiagnostics = new();

            List<MessageMethod> methods = BuildMessageMethods(rawMessages, defaultLocale, modelDiagnostics);
            HashSet<string> usedMethodNames = new(methods.Select(static method => method.MethodName), StringComparer.Ordinal);
            List<PluralMethod> pluralMethods = BuildI18NextPluralMethods(rawMessages, defaultLocale, usedMethodNames);

            return new GenerationModel(options, locales, defaultLocale, methods, pluralMethods, modelDiagnostics);
        }

        private static string ResolveDefaultLocale(IReadOnlyList<string> locales, string? configuredDefaultLocale)
        {
            if (!string.IsNullOrWhiteSpace(configuredDefaultLocale) && locales.Contains(configuredDefaultLocale!, StringComparer.Ordinal))
            {
                return configuredDefaultLocale!;
            }

            foreach (string locale in locales)
            {
                if (locale.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                    locale.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                {
                    return locale;
                }
            }

            return locales.Count > 0 ? locales[0] : "en";
        }

        private static List<MessageMethod> BuildMessageMethods(
            IReadOnlyList<RawMessage> rawMessages,
            string defaultLocale,
            List<Diagnostic> diagnostics)
        {
            List<MessageMethod> methods = new();
            HashSet<string> methodNames = new(StringComparer.Ordinal);

            foreach (IGrouping<string, RawMessage> group in rawMessages
                         .Where(static message => message.Format != MessageFormat.I18Next || !TrySplitI18NextPluralKey(message.Key, out _, out _, out _))
                         .GroupBy(static message => message.Key)
                         .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                MessageMethod method = MessageMethod.Create(group.Key, group.ToList(), defaultLocale, diagnostics);
                method.MethodName = Identifier.Unique(Identifier.ToPascal(group.Key), methodNames);
                methods.Add(method);
            }

            return methods;
        }

        private static List<PluralMethod> BuildI18NextPluralMethods(
            IReadOnlyList<RawMessage> rawMessages,
            string defaultLocale,
            HashSet<string> usedMethodNames)
        {
            List<RawMessage> i18nextVariants = new();
            foreach (RawMessage message in rawMessages)
            {
                if (message.Format != MessageFormat.I18Next || message.PluralCategory is not null)
                {
                    continue;
                }

                if (TrySplitI18NextPluralKey(message.Key, out string? baseKey, out string? category, out bool isOrdinal))
                {
                    i18nextVariants.Add(RawMessage.Plural(baseKey!, message.Locale, message.Format, category!, isOrdinal, message.Template, message.Path, null, message.Line));
                }
            }

            List<PluralMethod> methods = new();
            foreach (IGrouping<string, RawMessage> group in i18nextVariants.GroupBy(static message => message.Key + "\u001f" + message.IsOrdinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                List<RawMessage> groupMessages = group.ToList();
                string baseKey = groupMessages[0].Key;
                bool isOrdinal = groupMessages[0].IsOrdinal;
                string methodName = Identifier.ToPascal(isOrdinal ? baseKey + ".ordinal" : baseKey);
                if (usedMethodNames.Contains(methodName))
                {
                    methodName = Identifier.ToPascal(isOrdinal ? baseKey + ".ordinal" : baseKey + ".plural");
                }

                methodName = Identifier.Unique(methodName, usedMethodNames);
                methods.Add(PluralMethod.Create(baseKey, methodName, groupMessages, defaultLocale, isOrdinal));
            }

            return methods;
        }
    }

    private sealed class MessageMethod
    {
        private MessageMethod(
            string key,
            IReadOnlyDictionary<string, Template> templates,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Template>> pluralTemplates,
            IReadOnlyList<Parameter> parameters,
            string defaultLocale,
            string? selectorVariable,
            string definitionPath,
            int definitionLine)
        {
            Key = key;
            Templates = templates;
            PluralTemplates = pluralTemplates;
            Parameters = parameters;
            DefaultLocale = defaultLocale;
            SelectorVariable = selectorVariable;
            DefinitionPath = definitionPath;
            DefinitionLine = definitionLine;
            MethodName = string.Empty;
        }

        public string Key { get; }

        public string UsageKey => Key;

        public string DefinitionPath { get; }

        public int DefinitionLine { get; }

        public string MethodName { get; set; }

        public IReadOnlyDictionary<string, Template> Templates { get; }

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Template>> PluralTemplates { get; }

        public IReadOnlyList<Parameter> Parameters { get; }

        public string DefaultLocale { get; }

        public string? SelectorVariable { get; }

        public bool IsPlural => PluralTemplates.Count > 0;

        public static MessageMethod Create(string key, IReadOnlyList<RawMessage> messages, string defaultLocale, List<Diagnostic> diagnostics)
        {
            Dictionary<string, Template> templates = new(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, Template>> pluralTemplates = new(StringComparer.Ordinal);
            Dictionary<string, string> winningPaths = new(StringComparer.Ordinal);
            List<string> variables = new();
            string? selectorVariable = null;

            foreach (RawMessage message in messages.OrderBy(static message => message.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (message.PluralCategory is null)
                {
                    if (templates.ContainsKey(message.Locale))
                    {
                        diagnostics.Add(Diagnostic.Create(DuplicateMessage, Location.None, key, message.Locale, message.Path));
                    }

                    templates[message.Locale] = message.Template;
                    winningPaths[message.Locale] = message.Path;
                }
                else
                {
                    if (!pluralTemplates.TryGetValue(message.Locale, out Dictionary<string, Template>? perLocale))
                    {
                        perLocale = new Dictionary<string, Template>(StringComparer.Ordinal);
                        pluralTemplates[message.Locale] = perLocale;
                    }

                    perLocale[message.PluralCategory] = message.Template;
                    if (!string.IsNullOrEmpty(message.SelectorVariable))
                    {
                        selectorVariable ??= message.SelectorVariable;
                    }
                }

                AddVariables(variables, message.Template.Variables);
            }

            if (pluralTemplates.Count > 0 && !string.IsNullOrEmpty(selectorVariable))
            {
                MoveVariableToFront(variables, selectorVariable!);
            }
            else if (pluralTemplates.Count > 0)
            {
                AddVariables(variables, new[] { "count" });
                selectorVariable = "count";
            }

            RawMessage.SelectDefinition(messages, defaultLocale, out string definitionPath, out int definitionLine);
            return new MessageMethod(
                key,
                templates,
                pluralTemplates.ToDictionary(static item => item.Key, static item => (IReadOnlyDictionary<string, Template>)item.Value, StringComparer.Ordinal),
                Parameter.CreateMany(variables),
                defaultLocale,
                selectorVariable,
                definitionPath,
                definitionLine);
        }

        private static void AddVariables(List<string> variables, IEnumerable<string> newVariables)
        {
            foreach (string variable in newVariables)
            {
                if (!variables.Contains(variable, StringComparer.Ordinal))
                {
                    variables.Add(variable);
                }
            }
        }

        private static void MoveVariableToFront(List<string> variables, string variable)
        {
            variables.RemoveAll(item => item.Equals(variable, StringComparison.Ordinal));
            variables.Insert(0, variable);
        }
    }

    private sealed class PluralMethod
    {
        private PluralMethod(
            string key,
            string methodName,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Template>> templates,
            IReadOnlyList<Parameter> parameters,
            string defaultLocale,
            bool isOrdinal,
            string definitionPath,
            int definitionLine)
        {
            Key = key;
            MethodName = methodName;
            Templates = templates;
            Parameters = parameters;
            DefaultLocale = defaultLocale;
            IsOrdinal = isOrdinal;
            DefinitionPath = definitionPath;
            DefinitionLine = definitionLine;
        }

        public string Key { get; }

        public string UsageKey => IsOrdinal ? Key + ".ordinal" : Key;

        public string DefinitionPath { get; }

        public int DefinitionLine { get; }

        public string MethodName { get; }

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Template>> Templates { get; }

        public IReadOnlyList<Parameter> Parameters { get; }

        public string DefaultLocale { get; }

        public bool IsOrdinal { get; }

        public static PluralMethod Create(
            string key,
            string methodName,
            IReadOnlyList<RawMessage> messages,
            string defaultLocale,
            bool isOrdinal)
        {
            Dictionary<string, Dictionary<string, Template>> templates = new(StringComparer.Ordinal);
            List<string> variables = new() { "count" };

            foreach (RawMessage message in messages)
            {
                if (message.PluralCategory is null)
                {
                    continue;
                }

                if (!templates.TryGetValue(message.Locale, out Dictionary<string, Template>? perLocale))
                {
                    perLocale = new Dictionary<string, Template>(StringComparer.Ordinal);
                    templates[message.Locale] = perLocale;
                }

                perLocale[message.PluralCategory] = message.Template;
                foreach (string variable in message.Template.Variables)
                {
                    if (!variables.Contains(variable, StringComparer.Ordinal))
                    {
                        variables.Add(variable);
                    }
                }
            }

            RawMessage.SelectDefinition(messages, defaultLocale, out string definitionPath, out int definitionLine);
            return new PluralMethod(
                key,
                methodName,
                templates.ToDictionary(static item => item.Key, static item => (IReadOnlyDictionary<string, Template>)item.Value, StringComparer.Ordinal),
                Parameter.CreateMany(variables),
                defaultLocale,
                isOrdinal,
                definitionPath,
                definitionLine);
        }
    }

    private sealed class Parameter
    {
        private Parameter(string name, string identifier)
        {
            Name = name;
            Identifier = identifier;
        }

        public string Name { get; }

        public string Identifier { get; }

        public static IReadOnlyList<Parameter> CreateMany(IReadOnlyList<string> variables)
        {
            List<Parameter> parameters = new();
            HashSet<string> used = new(StringComparer.Ordinal);
            HashSet<string> usedVariableNames = new(StringComparer.Ordinal);
            foreach (string variable in variables)
            {
                if (!usedVariableNames.Add(variable))
                {
                    continue;
                }

                string identifier = ZeroI18nGenerator.Identifier.ToCamel(variable);
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    identifier = "value";
                }

                string unique = ZeroI18nGenerator.Identifier.Unique(identifier, used);
                parameters.Add(new Parameter(variable, ZeroI18nGenerator.Identifier.EscapeKeyword(unique)));
            }

            return parameters;
        }
    }

    private sealed class Template
    {
        private Template(IReadOnlyList<TemplatePart> parts)
        {
            Parts = parts;
            Variables = parts
                .Where(static part => part.IsVariable)
                .Select(static part => part.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyList<TemplatePart> Parts { get; }

        public IReadOnlyList<string> Variables { get; }

        public static Template Parse(string value, MessageFormat format, IReadOnlyDictionary<string, string>? terms = null)
        {
            if (format == MessageFormat.Fluent)
            {
                return ParseFluent(value, terms);
            }

            return format == MessageFormat.I18Next
                ? ParseI18Next(value)
                : ParseMessageFormat(value);
        }

        private static Template ParseFluent(string value, IReadOnlyDictionary<string, string>? terms)
        {
            List<TemplatePart> parts = new();
            StringBuilder literal = new();

            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];

                if (current == '{')
                {
                    int end = FindClosingBraceInFluent(value, index + 1);
                    if (end >= 0)
                    {
                        string token = value.Substring(index + 1, end - index - 1).Trim();
                        // String literal in expression: {"{"}, {"\""}, {"hello"}
                        if (token.StartsWith("\"") && token.EndsWith("\"") && token.Length >= 2)
                        {
                            string unescaped = UnescapeFluentStringLiteral(token);
                            literal.Append(unescaped);
                            index = end;
                            continue;
                        }

                        // Variable: {$varName} or { $varName }
                        if (token.StartsWith("$", StringComparison.Ordinal))
                        {
                            string varName = token.Substring(1).Trim();
                            string ident = Identifier.ToCamel(varName);
                            if (string.IsNullOrEmpty(ident))
                            {
                                ident = varName;
                            }

                            if (Identifier.IsValidMessageVariable(ident))
                            {
                                FlushLiteral(parts, literal);
                                parts.Add(TemplatePart.Variable(ident));
                                index = end;
                                continue;
                            }
                        }

                        // Term: {-term-name}
                        if (token.StartsWith("-", StringComparison.Ordinal))
                        {
                            string termId = token.Substring(1).Trim();
                            if (terms != null && terms.TryGetValue(termId, out string? termValue))
                            {
                                literal.Append(termValue);
                                index = end;
                                continue;
                            }
                        }

                        // Function call with variable: e.g. NUMBER($count)
                        int dollarIdx = token.IndexOf('$');
                        if (dollarIdx >= 0)
                        {
                            int varEnd = dollarIdx + 1;
                            while (varEnd < token.Length && (char.IsLetterOrDigit(token[varEnd]) || token[varEnd] == '_' || token[varEnd] == '-'))
                            {
                                varEnd++;
                            }
                            string varName = token.Substring(dollarIdx + 1, varEnd - dollarIdx - 1);
                            string ident = Identifier.ToCamel(varName);
                            if (!string.IsNullOrEmpty(ident) && Identifier.IsValidMessageVariable(ident))
                            {
                                FlushLiteral(parts, literal);
                                parts.Add(TemplatePart.Variable(ident));
                                index = end;
                                continue;
                            }
                        }

                        // Identifier without $: {count}
                        if (Identifier.IsValidMessageVariable(token))
                        {
                            FlushLiteral(parts, literal);
                            parts.Add(TemplatePart.Variable(token));
                            index = end;
                            continue;
                        }

                        literal.Append(value, index, end - index + 1);
                        index = end;
                        continue;
                    }
                }

                literal.Append(current);
            }

            FlushLiteral(parts, literal);
            return new Template(parts);
        }

        private static string UnescapeFluentStringLiteral(string token)
        {
            if (token.Length < 2)
            {
                return string.Empty;
            }

            string inner = token.Substring(1, token.Length - 2);
            StringBuilder sb = new();
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    char next = inner[i + 1];
                    switch (next)
                    {
                        case '"':
                            sb.Append('"');
                            i++;
                            break;
                        case '\\':
                            sb.Append('\\');
                            i++;
                            break;
                        case 'u':
                            if (i + 5 < inner.Length && int.TryParse(inner.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int uVal))
                            {
                                sb.Append((char)uVal);
                                i += 5;
                            }
                            else
                            {
                                sb.Append(next);
                                i++;
                            }
                            break;
                        default:
                            sb.Append(next);
                            i++;
                            break;
                    }
                }
                else
                {
                    sb.Append(inner[i]);
                }
            }

            return sb.ToString();
        }

        public Template WithRequiredVariable(string variable)
        {
            if (Variables.Contains(variable, StringComparer.Ordinal))
            {
                return this;
            }

            List<TemplatePart> parts = new(Parts)
            {
                TemplatePart.Variable(variable)
            };
            return new Template(parts);
        }

        private static Template ParseMessageFormat(string value)
        {
            List<TemplatePart> parts = new();
            StringBuilder literal = new();

            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '\\' && index + 1 < value.Length && (value[index + 1] == '{' || value[index + 1] == '}' || value[index + 1] == '\\'))
                {
                    literal.Append(value[index + 1]);
                    index++;
                    continue;
                }

                if (current == '{')
                {
                    int end = FindClosingBrace(value, index + 1, '}');
                    if (end >= 0)
                    {
                        string token = value.Substring(index + 1, end - index - 1).Trim();
                        if (Identifier.IsValidMessageVariable(token))
                        {
                            FlushLiteral(parts, literal);
                            parts.Add(TemplatePart.Variable(token));
                        }
                        else
                        {
                            literal.Append(value, index, end - index + 1);
                        }

                        index = end;
                        continue;
                    }
                }

                literal.Append(current);
            }

            FlushLiteral(parts, literal);
            return new Template(parts);
        }

        private static Template ParseI18Next(string value)
        {
            List<TemplatePart> parts = new();
            StringBuilder literal = new();

            for (int index = 0; index < value.Length; index++)
            {
                if (index + 1 < value.Length && value[index] == '{' && value[index + 1] == '{')
                {
                    int end = value.IndexOf("}}", index + 2, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        string token = value.Substring(index + 2, end - index - 2).Trim();
                        if (token.StartsWith("-", StringComparison.Ordinal))
                        {
                            token = token.Substring(1).TrimStart();
                        }

                        int commaIndex = token.IndexOf(',');
                        if (commaIndex >= 0)
                        {
                            token = token.Substring(0, commaIndex).Trim();
                        }

                        if (Identifier.IsValidMessageVariable(token))
                        {
                            FlushLiteral(parts, literal);
                            parts.Add(TemplatePart.Variable(token));
                        }
                        else
                        {
                            literal.Append(value, index, end - index + 2);
                        }

                        index = end + 1;
                        continue;
                    }
                }

                literal.Append(value[index]);
            }

            FlushLiteral(parts, literal);
            return new Template(parts);
        }

        private static int FindClosingBrace(string value, int start, char close)
        {
            for (int index = start; index < value.Length; index++)
            {
                if (value[index] == close && (index == 0 || value[index - 1] != '\\'))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int FindClosingBraceInFluent(string value, int start)
        {
            bool inQuote = false;
            for (int index = start; index < value.Length; index++)
            {
                char c = value[index];
                if (c == '"' && (index == 0 || value[index - 1] != '\\'))
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (!inQuote && c == '}' && (index == 0 || value[index - 1] != '\\'))
                {
                    return index;
                }
            }

            return -1;
        }

        private static void FlushLiteral(List<TemplatePart> parts, StringBuilder literal)
        {
            if (literal.Length == 0)
            {
                return;
            }

            parts.Add(TemplatePart.Text(literal.ToString()));
            literal.Clear();
        }
    }

    private readonly struct TemplatePart
    {
        private TemplatePart(string value, bool isVariable)
        {
            Value = value;
            IsVariable = isVariable;
        }

        public string Value { get; }

        public bool IsVariable { get; }

        public static TemplatePart Text(string value)
        {
            return new TemplatePart(value, false);
        }

        public static TemplatePart Variable(string value)
        {
            return new TemplatePart(value, true);
        }
    }

    private static class SourceEmitter
    {
        public static string Emit(GenerationModel model)
        {
            StringBuilder builder = new();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine("#pragma warning disable CS1591, CS8618, CS8604");
            builder.AppendLine("namespace " + model.Options.Namespace);
            builder.AppendLine("{");
            builder.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"ZeroI18n\", \"1.1.0\")]");
            builder.AppendLine("    " + model.Options.AccessModifier + " static partial class " + model.Options.ClassName);
            builder.AppendLine("    {");
            builder.AppendLine("        public const string DefaultLocale = " + Literal(model.DefaultLocale) + ";");
            builder.AppendLine("        private static string s_currentLocale = DefaultLocale;");
            builder.AppendLine("        private static readonly string[] s_supportedLocales = new string[]");
            builder.AppendLine("        {");
            foreach (string locale in model.Locales)
            {
                builder.AppendLine("            " + Literal(locale) + ",");
            }

            builder.AppendLine("        };");
            builder.AppendLine();
            builder.AppendLine("        public static global::System.Collections.Generic.IReadOnlyList<string> SupportedLocales => s_supportedLocales;");
            builder.AppendLine();
            builder.AppendLine("        public static event global::System.Action? CurrentLocaleChanged;");
            builder.AppendLine();
            builder.AppendLine("        public static string CurrentLocale");
            builder.AppendLine("        {");
            builder.AppendLine("            get => s_currentLocale;");
            builder.AppendLine("            set");
            builder.AppendLine("            {");
            builder.AppendLine("                string normalized = NormalizeLocale(value);");
            builder.AppendLine("                if (!string.Equals(s_currentLocale, normalized, global::System.StringComparison.Ordinal))");
            builder.AppendLine("                {");
            builder.AppendLine("                    s_currentLocale = normalized;");
            builder.AppendLine("                    CurrentLocaleChanged?.Invoke();");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();
            EmitLocaleHelpers(builder, model);
            builder.AppendLine();
            EmitRuntimeHelpers(builder);

            foreach (MessageMethod method in model.Methods)
            {
                builder.AppendLine();
                EmitMessageMethod(builder, method);
            }

            foreach (PluralMethod method in model.PluralMethods)
            {
                builder.AppendLine();
                EmitPluralMethod(builder, method);
            }

            builder.AppendLine();
            EmitGetStringMethod(builder, model);

            builder.AppendLine("    }");
            builder.AppendLine();
            EmitLocalizationManager(builder, model);
            builder.AppendLine();
            EmitKeyAttribute(builder);
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void EmitLocaleHelpers(StringBuilder builder, GenerationModel model)
        {
            builder.AppendLine("        public static bool IsSupportedLocale(string? locale) => locale switch");
            builder.AppendLine("        {");
            foreach (string locale in model.Locales)
            {
                builder.AppendLine("            " + Literal(locale) + " => true,");
            }

            builder.AppendLine("            _ => false");
            builder.AppendLine("        };");
            builder.AppendLine();
            builder.AppendLine("        public static string NormalizeLocale(string? locale) => IsSupportedLocale(locale) ? locale! : DefaultLocale;");
        }

        private static void EmitRuntimeHelpers(StringBuilder builder)
        {
            builder.AppendLine("        private static string __s(object? value)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (value is null)");
            builder.AppendLine("            {");
            builder.AppendLine("                return string.Empty;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return value is string text");
            builder.AppendLine("                ? text");
            builder.AppendLine("                : global::System.Convert.ToString(value, global::System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        private static string __plural(string? locale, object? count, bool ordinal)");
            builder.AppendLine("        {");
            builder.AppendLine("            double number = __number(count);");
            builder.AppendLine("            string language = __language(locale);");
            builder.AppendLine("            if (ordinal)");
            builder.AppendLine("            {");
            builder.AppendLine("                if (language == \"en\")");
            builder.AppendLine("                {");
            builder.AppendLine("                    double mod10 = number % 10;");
            builder.AppendLine("                    double mod100 = number % 100;");
            builder.AppendLine("                    if (mod10 == 1 && mod100 != 11) return \"one\";");
            builder.AppendLine("                    if (mod10 == 2 && mod100 != 12) return \"two\";");
            builder.AppendLine("                    if (mod10 == 3 && mod100 != 13) return \"few\";");
            builder.AppendLine("                }");
            builder.AppendLine();
            builder.AppendLine("                return \"other\";");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (language == \"zh\" || language == \"ja\" || language == \"ko\")");
            builder.AppendLine("            {");
            builder.AppendLine("                return \"other\";");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (language == \"fr\")");
            builder.AppendLine("            {");
            builder.AppendLine("                return number >= 0 && number < 2 ? \"one\" : \"other\";");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return number == 1 ? \"one\" : \"other\";");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        private static double __number(object? value)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (value is null)");
            builder.AppendLine("            {");
            builder.AppendLine("                return 0;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (value is global::System.IConvertible convertible)");
            builder.AppendLine("            {");
            builder.AppendLine("                try");
            builder.AppendLine("                {");
            builder.AppendLine("                    return convertible.ToDouble(global::System.Globalization.CultureInfo.InvariantCulture);");
            builder.AppendLine("                }");
            builder.AppendLine("                catch (global::System.FormatException)");
            builder.AppendLine("                {");
            builder.AppendLine("                }");
            builder.AppendLine("                catch (global::System.InvalidCastException)");
            builder.AppendLine("                {");
            builder.AppendLine("                }");
            builder.AppendLine("                catch (global::System.OverflowException)");
            builder.AppendLine("                {");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            return 0;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        private static string __language(string? locale)");
            builder.AppendLine("        {");
            builder.AppendLine("            string normalized = NormalizeLocale(locale);");
            builder.AppendLine("            int separator = normalized.IndexOf('-');");
            builder.AppendLine("            return separator >= 0 ? normalized.Substring(0, separator) : normalized;");
            builder.AppendLine("        }");
        }

        private static void EmitMessageMethod(StringBuilder builder, MessageMethod method)
        {
            EmitGeneratedMethodAttribute(builder, method.UsageKey, method.DefinitionPath, method.DefinitionLine);
            builder.AppendLine("        public static string " + method.MethodName + "(" + BuildParameters(method.Parameters) + ") => " + method.MethodName + "ForLocale(CurrentLocale" + BuildArgumentSuffix(method.Parameters) + ");");
            EmitGeneratedMethodAttribute(builder, method.UsageKey, method.DefinitionPath, method.DefinitionLine);
            builder.AppendLine("        public static string " + method.MethodName + "ForLocale(string? locale" + BuildParameterSuffix(method.Parameters) + ")");
            builder.AppendLine("        {");
            builder.AppendLine("            string __locale = NormalizeLocale(locale);");

            if (method.IsPlural)
            {
                Parameter count = FindCountParameter(method.Parameters);
                builder.AppendLine("            string __category = __plural(__locale, " + count.Identifier + ", false);");
                builder.AppendLine("            return __locale switch");
                builder.AppendLine("            {");
                foreach (string locale in OrderedLocales(method.PluralTemplates.Keys, method.DefaultLocale))
                {
                    IReadOnlyDictionary<string, Template> variants = method.PluralTemplates[locale];
                    builder.AppendLine("                " + Literal(locale) + " => __category switch");
                    builder.AppendLine("                {");
                    EmitCategorySwitchCases(builder, variants, method.Parameters, "                    ");
                    builder.AppendLine("                },");
                }

                IReadOnlyDictionary<string, Template> fallback = ResolveFallbackPluralTemplates(method.PluralTemplates, method.DefaultLocale);
                builder.AppendLine("                _ => __category switch");
                builder.AppendLine("                {");
                EmitCategorySwitchCases(builder, fallback, method.Parameters, "                    ");
                builder.AppendLine("                }");
                builder.AppendLine("            };");
            }
            else
            {
                builder.AppendLine("            return __locale switch");
                builder.AppendLine("            {");
                foreach (string locale in OrderedLocales(method.Templates.Keys, method.DefaultLocale))
                {
                    builder.AppendLine("                " + Literal(locale) + " => " + EmitTemplate(method.Templates[locale], method.Parameters) + ",");
                }

                Template fallback = ResolveFallbackTemplate(method.Templates, method.DefaultLocale);
                builder.AppendLine("                _ => " + EmitTemplate(fallback, method.Parameters));
                builder.AppendLine("            };");
            }

            builder.AppendLine("        }");
        }

        private static void EmitPluralMethod(StringBuilder builder, PluralMethod method)
        {
            EmitGeneratedMethodAttribute(builder, method.UsageKey, method.DefinitionPath, method.DefinitionLine);
            builder.AppendLine("        public static string " + method.MethodName + "(" + BuildParameters(method.Parameters) + ") => " + method.MethodName + "ForLocale(CurrentLocale" + BuildArgumentSuffix(method.Parameters) + ");");
            EmitGeneratedMethodAttribute(builder, method.UsageKey, method.DefinitionPath, method.DefinitionLine);
            builder.AppendLine("        public static string " + method.MethodName + "ForLocale(string? locale" + BuildParameterSuffix(method.Parameters) + ")");
            builder.AppendLine("        {");
            Parameter count = FindCountParameter(method.Parameters);
            builder.AppendLine("            string __locale = NormalizeLocale(locale);");
            builder.AppendLine("            string __category = __plural(__locale, " + count.Identifier + ", " + (method.IsOrdinal ? "true" : "false") + ");");
            builder.AppendLine("            return __locale switch");
            builder.AppendLine("            {");
            foreach (string locale in OrderedLocales(method.Templates.Keys, method.DefaultLocale))
            {
                IReadOnlyDictionary<string, Template> variants = method.Templates[locale];
                builder.AppendLine("                " + Literal(locale) + " => __category switch");
                builder.AppendLine("                {");
                EmitCategorySwitchCases(builder, variants, method.Parameters, "                    ");
                builder.AppendLine("                },");
            }

            IReadOnlyDictionary<string, Template> fallback = ResolveFallbackPluralTemplates(method.Templates, method.DefaultLocale);
            builder.AppendLine("                _ => __category switch");
            builder.AppendLine("                {");
            EmitCategorySwitchCases(builder, fallback, method.Parameters, "                    ");
            builder.AppendLine("                }");
            builder.AppendLine("            };");
            builder.AppendLine("        }");
        }

        private static void EmitGetStringMethod(StringBuilder builder, GenerationModel model)
        {
            builder.AppendLine("        [global::System.CodeDom.Compiler.GeneratedCode(\"ZeroI18n\", \"1.1.0\")]");
            builder.AppendLine("        [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
            builder.AppendLine("        public static string GetString(string key) => key switch");
            builder.AppendLine("        {");

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (MessageMethod method in model.Methods.Where(m => m.Parameters.Count == 0 && !m.IsPlural))
            {
                if (seen.Add(method.Key))
                {
                    builder.AppendLine("            " + Literal(method.Key) + " => " + method.MethodName + "(),");
                }
                if (seen.Add(method.MethodName))
                {
                    builder.AppendLine("            " + Literal(method.MethodName) + " => " + method.MethodName + "(),");
                }
                if (method.Key.IndexOf('-') >= 0)
                {
                    string dotted = method.Key.Replace('-', '.');
                    if (seen.Add(dotted))
                    {
                        builder.AppendLine("            " + Literal(dotted) + " => " + method.MethodName + "(),");
                    }
                }
            }

            builder.AppendLine("            _ => key");
            builder.AppendLine("        };");
        }

        private static void EmitLocalizationManager(StringBuilder builder, GenerationModel model)
        {
            builder.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"ZeroI18n\", \"1.1.0\")]");
            builder.AppendLine("    [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
            builder.AppendLine("    " + model.Options.AccessModifier + " sealed class LocalizationManager : global::System.ComponentModel.INotifyPropertyChanged");
            builder.AppendLine("    {");
            builder.AppendLine("        public static LocalizationManager Instance { get; } = new LocalizationManager();");
            builder.AppendLine();
            builder.AppendLine("        private LocalizationManager()");
            builder.AppendLine("        {");
            builder.AppendLine("            " + model.Options.ClassName + ".CurrentLocaleChanged += OnCurrentLocaleChanged;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        public event global::System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;");
            builder.AppendLine();
            builder.AppendLine("        public string CurrentLocale");
            builder.AppendLine("        {");
            builder.AppendLine("            get => " + model.Options.ClassName + ".CurrentLocale;");
            builder.AppendLine("            set => " + model.Options.ClassName + ".CurrentLocale = value;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        public string this[string key] => " + model.Options.ClassName + ".GetString(key);");
            builder.AppendLine();
            builder.AppendLine("        private void OnCurrentLocaleChanged()");
            builder.AppendLine("        {");
            builder.AppendLine("            PropertyChanged?.Invoke(this, new global::System.ComponentModel.PropertyChangedEventArgs(string.Empty));");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
        }

        private static void EmitCategorySwitchCases(StringBuilder builder, IReadOnlyDictionary<string, Template> variants, IReadOnlyList<Parameter> parameters, string indent)
        {
            foreach (string category in variants.Keys.Where(static category => IsPluralCategory(category)).OrderBy(static category => category, StringComparer.Ordinal))
            {
                if (category.Equals("other", StringComparison.Ordinal))
                {
                    continue;
                }

                builder.AppendLine(indent + Literal(category) + " => " + EmitTemplate(variants[category], parameters) + ",");
            }

            Template fallback = variants.TryGetValue("other", out Template? other)
                ? other
                : variants.OrderBy(static item => item.Key, StringComparer.Ordinal).First().Value;
            builder.AppendLine(indent + "_ => " + EmitTemplate(fallback, parameters));
        }

        private static Template ResolveFallbackTemplate(IReadOnlyDictionary<string, Template> templates, string defaultLocale)
        {
            return templates.TryGetValue(defaultLocale, out Template? template)
                ? template
                : templates.OrderBy(static item => item.Key, StringComparer.Ordinal).First().Value;
        }

        private static IReadOnlyDictionary<string, Template> ResolveFallbackPluralTemplates(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, Template>> templates,
            string defaultLocale)
        {
            return templates.TryGetValue(defaultLocale, out IReadOnlyDictionary<string, Template>? template)
                ? template
                : templates.OrderBy(static item => item.Key, StringComparer.Ordinal).First().Value;
        }

        private static IEnumerable<string> OrderedLocales(IEnumerable<string> locales, string defaultLocale)
        {
            return locales
                .Where(locale => !locale.Equals(defaultLocale, StringComparison.Ordinal))
                .OrderBy(static locale => locale, StringComparer.Ordinal)
                .Concat(locales.Where(locale => locale.Equals(defaultLocale, StringComparison.Ordinal)));
        }

        private static Parameter FindCountParameter(IReadOnlyList<Parameter> parameters)
        {
            foreach (Parameter parameter in parameters)
            {
                if (parameter.Name.Equals("count", StringComparison.Ordinal))
                {
                    return parameter;
                }
            }

            return parameters[0];
        }

        private static string BuildParameters(IReadOnlyList<Parameter> parameters)
        {
            return string.Join(", ", parameters.Select(static parameter => "object? " + parameter.Identifier));
        }

        private static string BuildParameterSuffix(IReadOnlyList<Parameter> parameters)
        {
            if (parameters.Count == 0)
            {
                return string.Empty;
            }

            return ", " + BuildParameters(parameters);
        }

        private static string BuildArgumentSuffix(IReadOnlyList<Parameter> parameters)
        {
            if (parameters.Count == 0)
            {
                return string.Empty;
            }

            return ", " + string.Join(", ", parameters.Select(static parameter => parameter.Identifier));
        }

        private static string EmitTemplate(Template template, IReadOnlyList<Parameter> parameters)
        {
            if (template.Parts.Count == 0)
            {
                return "string.Empty";
            }

            Dictionary<string, string> parameterMap = parameters.ToDictionary(static parameter => parameter.Name, static parameter => parameter.Identifier, StringComparer.Ordinal);
            List<string> expressions = new();
            foreach (TemplatePart part in template.Parts)
            {
                if (part.IsVariable)
                {
                    if (parameterMap.TryGetValue(part.Value, out string? identifier))
                    {
                        expressions.Add("__s(" + identifier + ")");
                    }
                }
                else if (part.Value.Length > 0)
                {
                    expressions.Add(Literal(part.Value));
                }
            }

            if (expressions.Count == 0)
            {
                return "string.Empty";
            }

            if (expressions.Count == 1)
            {
                return expressions[0];
            }

            return "string.Concat(" + string.Join(", ", expressions) + ")";
        }

        private static void EmitKeyAttribute(StringBuilder builder)
        {
            builder.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)]");
            builder.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"ZeroI18n\", \"1.1.0\")]");
            builder.AppendLine("    internal sealed class ZeroI18nKeyAttribute : global::System.Attribute");
            builder.AppendLine("    {");
            builder.AppendLine("        public ZeroI18nKeyAttribute(string key, string path, int line)");
            builder.AppendLine("        {");
            builder.AppendLine("            Key = key;");
            builder.AppendLine("            Path = path;");
            builder.AppendLine("            Line = line;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        public string Key { get; }");
            builder.AppendLine("        public string Path { get; }");
            builder.AppendLine("        public int Line { get; }");
            builder.AppendLine("    }");
        }

        private static void EmitGeneratedMethodAttribute(StringBuilder builder, string key, string path, int line)
        {
            builder.AppendLine("        [global::System.CodeDom.Compiler.GeneratedCode(\"ZeroI18n\", \"1.1.0\")]");
            builder.AppendLine("        [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
            builder.AppendLine("        [ZeroI18nKey(" + Literal(key) + ", " + Literal(path) + ", " + line.ToString(CultureInfo.InvariantCulture) + ")]");
        }

        private static string Literal(string value)
        {
            return SymbolDisplay.FormatLiteral(value, true);
        }
    }

    private static bool TrySplitI18NextPluralKey(string key, out string? baseKey, out string? category, out bool isOrdinal)
    {
        foreach (string pluralCategory in new[] { "zero", "one", "two", "few", "many", "other" })
        {
            string ordinalSuffix = "_ordinal_" + pluralCategory;
            if (key.EndsWith(ordinalSuffix, StringComparison.Ordinal))
            {
                baseKey = key.Substring(0, key.Length - ordinalSuffix.Length);
                category = pluralCategory;
                isOrdinal = true;
                return !string.IsNullOrEmpty(baseKey);
            }

            string suffix = "_" + pluralCategory;
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                baseKey = key.Substring(0, key.Length - suffix.Length);
                category = pluralCategory;
                isOrdinal = false;
                return !string.IsNullOrEmpty(baseKey);
            }
        }

        baseKey = null;
        category = null;
        isOrdinal = false;
        return false;
    }

    private static bool IsPluralCategory(string value)
    {
        return value is "zero" or "one" or "two" or "few" or "many" or "other";
    }

    private static class Identifier
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while"
        };

        public static string ToPascal(string value)
        {
            List<string> parts = SplitWords(value);
            if (parts.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            foreach (string part in parts)
            {
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    builder.Append(part.Substring(1));
                }
            }

            if (builder.Length > 0 && char.IsDigit(builder[0]))
            {
                builder.Insert(0, '_');
            }

            return EscapeKeyword(builder.ToString());
        }

        public static string ToCamel(string value)
        {
            string pascal = ToPascal(value);
            if (string.IsNullOrEmpty(pascal))
            {
                return string.Empty;
            }

            if (pascal[0] == '@')
            {
                pascal = pascal.Substring(1);
            }

            return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        }

        public static string EscapeKeyword(string value)
        {
            return Keywords.Contains(value) ? "@" + value : value;
        }

        public static string Unique(string value, HashSet<string> used)
        {
            if (string.IsNullOrEmpty(value))
            {
                value = "Message";
            }

            string baseValue = value;
            int index = 2;
            while (!used.Add(value))
            {
                value = baseValue + index.ToString(CultureInfo.InvariantCulture);
                index++;
            }

            return value;
        }

        public static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            for (int index = 1; index < value.Length; index++)
            {
                if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsValidMessageVariable(string value)
        {
            return IsValidIdentifier(value);
        }

        private static List<string> SplitWords(string value)
        {
            List<string> words = new();
            StringBuilder current = new();
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '_')
                {
                    if (character == '_')
                    {
                        Flush(words, current);
                    }
                    else
                    {
                        current.Append(character);
                    }
                }
                else
                {
                    Flush(words, current);
                }
            }

            Flush(words, current);
            return words;
        }

        private static void Flush(List<string> words, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            words.Add(current.ToString());
            current.Clear();
        }
    }

    private abstract class JsonValue
    {
    }

    private sealed class JsonObject : JsonValue
    {
        public JsonObject(List<JsonProperty> properties)
        {
            Properties = properties;
        }

        public List<JsonProperty> Properties { get; }

        public JsonValue? TryGet(string name)
        {
            foreach (JsonProperty property in Properties)
            {
                if (property.Name == name)
                {
                    return property.Value;
                }
            }

            return null;
        }
    }

    private sealed class JsonArray : JsonValue
    {
        public JsonArray(List<JsonValue> items)
        {
            Items = items;
        }

        public List<JsonValue> Items { get; }
    }

    private sealed class JsonString : JsonValue
    {
        public JsonString(string value)
        {
            Value = value;
        }

        public string Value { get; }
    }

    private sealed class JsonPrimitive : JsonValue
    {
        private JsonPrimitive()
        {
        }

        public static JsonPrimitive Instance { get; } = new();
    }

    private readonly struct JsonProperty
    {
        public JsonProperty(string name, JsonValue value, int line)
        {
            Name = name;
            Value = value;
            Line = line;
        }

        public string Name { get; }

        public JsonValue Value { get; }

        public int Line { get; }
    }

    private sealed class JsonParseException : Exception
    {
        public JsonParseException(string message)
            : base(message)
        {
        }
    }

    private sealed class JsonParser
    {
        private readonly string _text;
        private int _index;

        private JsonParser(string text)
        {
            _text = text;
        }

        public static JsonValue Parse(string text)
        {
            JsonParser parser = new(text);
            JsonValue value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.IsEnd)
            {
                parser.Fail("unexpected trailing content");
            }

            return value;
        }

        private bool IsEnd => _index >= _text.Length;

        private JsonValue ParseValue()
        {
            SkipWhitespace();
            if (IsEnd)
            {
                Fail("unexpected end of file");
            }

            char current = _text[_index];
            if (current == '{')
            {
                return ParseObject();
            }

            if (current == '[')
            {
                return ParseArray();
            }

            if (current == '"')
            {
                return new JsonString(ParseString());
            }

            if (current == 't')
            {
                ParseLiteral("true");
                return JsonPrimitive.Instance;
            }

            if (current == 'f')
            {
                ParseLiteral("false");
                return JsonPrimitive.Instance;
            }

            if (current == 'n')
            {
                ParseLiteral("null");
                return JsonPrimitive.Instance;
            }

            if (current == '-' || char.IsDigit(current))
            {
                ParseNumber();
                return JsonPrimitive.Instance;
            }

            Fail("unexpected character '" + current + "'");
            return JsonPrimitive.Instance;
        }

        private JsonObject ParseObject()
        {
            Expect('{');
            SkipWhitespace();
            List<JsonProperty> properties = new();
            if (TryConsume('}'))
            {
                return new JsonObject(properties);
            }

            while (true)
            {
                SkipWhitespace();
                if (IsEnd || _text[_index] != '"')
                {
                    Fail("expected a property name");
                }

                int line = LineAt(_index);
                string name = ParseString();
                SkipWhitespace();
                Expect(':');
                JsonValue value = ParseValue();
                properties.Add(new JsonProperty(name, value, line));
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    return new JsonObject(properties);
                }

                Expect(',');
            }
        }

        private JsonArray ParseArray()
        {
            Expect('[');
            SkipWhitespace();
            List<JsonValue> items = new();
            if (TryConsume(']'))
            {
                return new JsonArray(items);
            }

            while (true)
            {
                items.Add(ParseValue());
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    return new JsonArray(items);
                }

                Expect(',');
            }
        }

        private string ParseString()
        {
            Expect('"');
            StringBuilder builder = new();
            while (!IsEnd)
            {
                char current = _text[_index++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }

                if (IsEnd)
                {
                    Fail("unterminated escape sequence");
                }

                char escaped = _text[_index++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escaped);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        builder.Append(ParseUnicodeEscape());
                        break;
                    default:
                        Fail("unsupported escape sequence '\\" + escaped + "'");
                        break;
                }
            }

            Fail("unterminated string");
            return string.Empty;
        }

        private char ParseUnicodeEscape()
        {
            if (_index + 4 > _text.Length)
            {
                Fail("incomplete unicode escape");
            }

            int value = 0;
            for (int offset = 0; offset < 4; offset++)
            {
                char character = _text[_index++];
                value <<= 4;
                if (character >= '0' && character <= '9')
                {
                    value += character - '0';
                }
                else if (character >= 'a' && character <= 'f')
                {
                    value += character - 'a' + 10;
                }
                else if (character >= 'A' && character <= 'F')
                {
                    value += character - 'A' + 10;
                }
                else
                {
                    Fail("invalid unicode escape");
                }
            }

            return (char)value;
        }

        private void ParseNumber()
        {
            if (TryConsume('-'))
            {
            }

            if (TryConsume('0'))
            {
            }
            else
            {
                ConsumeDigits();
            }

            if (TryConsume('.'))
            {
                ConsumeDigits();
            }

            if (!IsEnd && (_text[_index] == 'e' || _text[_index] == 'E'))
            {
                _index++;
                if (!IsEnd && (_text[_index] == '+' || _text[_index] == '-'))
                {
                    _index++;
                }

                ConsumeDigits();
            }
        }

        private void ConsumeDigits()
        {
            if (IsEnd || !char.IsDigit(_text[_index]))
            {
                Fail("expected a digit");
            }

            while (!IsEnd && char.IsDigit(_text[_index]))
            {
                _index++;
            }
        }

        private void ParseLiteral(string literal)
        {
            for (int offset = 0; offset < literal.Length; offset++)
            {
                if (_index + offset >= _text.Length || _text[_index + offset] != literal[offset])
                {
                    Fail("expected '" + literal + "'");
                }
            }

            _index += literal.Length;
        }

        private int LineAt(int index)
        {
            int line = 1;
            int end = Math.Min(index, _text.Length);
            for (int position = 0; position < end; position++)
            {
                if (_text[position] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private void SkipWhitespace()
        {
            while (!IsEnd && char.IsWhiteSpace(_text[_index]))
            {
                _index++;
            }
        }

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (IsEnd || _text[_index] != expected)
            {
                Fail("expected '" + expected + "'");
            }

            _index++;
        }

        private bool TryConsume(char value)
        {
            SkipWhitespace();
            if (!IsEnd && _text[_index] == value)
            {
                _index++;
                return true;
            }

            return false;
        }

        private void Fail(string message)
        {
            throw new JsonParseException(message + " at offset " + _index.ToString(CultureInfo.InvariantCulture));
        }
    }

    private sealed class FluentParseException : Exception
    {
        public FluentParseException(string message)
            : base(message)
        {
        }
    }

    private static class FluentParser
    {
        public static void Parse(
            string text,
            string keyPrefix,
            LocaleFileInfo fileInfo,
            string path,
            List<RawMessage> messages,
            List<Diagnostic> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Dictionary<string, string> terms = new(StringComparer.Ordinal);
            List<FluentEntry> entries = new();
            FluentEntry? currentEntry = null;

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                bool isIndented = char.IsWhiteSpace(line[0]);

                if (!isIndented)
                {
                    int equalsIdx = line.IndexOf('=');
                    if (equalsIdx < 0)
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidFluent, Location.None, path, $"Line {lineIndex + 1}: expected '=' in message definition: '{line}'"));
                        continue;
                    }

                    string rawId = line.Substring(0, equalsIdx).Trim();
                    string initialValue = line.Substring(equalsIdx + 1).Trim();
                    bool isTerm = rawId.StartsWith("-", StringComparison.Ordinal);
                    string id = isTerm ? rawId.Substring(1).Trim() : rawId;

                    if (string.IsNullOrEmpty(id))
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidFluent, Location.None, path, $"Line {lineIndex + 1}: missing identifier before '='"));
                        continue;
                    }

                    currentEntry = new FluentEntry(id, isTerm, lineIndex + 1);
                    if (!string.IsNullOrEmpty(initialValue))
                    {
                        currentEntry.ValueLines.Add(initialValue);
                    }

                    entries.Add(currentEntry);
                }
                else
                {
                    if (currentEntry is null)
                    {
                        diagnostics.Add(Diagnostic.Create(InvalidFluent, Location.None, path, $"Line {lineIndex + 1}: indented line without preceding entry: '{line}'"));
                        continue;
                    }

                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith(".", StringComparison.Ordinal))
                    {
                        int attrEqualsIdx = trimmed.IndexOf('=');
                        if (attrEqualsIdx < 0)
                        {
                            diagnostics.Add(Diagnostic.Create(InvalidFluent, Location.None, path, $"Line {lineIndex + 1}: expected '=' in attribute definition: '{line}'"));
                            continue;
                        }

                        string attrId = trimmed.Substring(1, attrEqualsIdx - 1).Trim();
                        string attrVal = trimmed.Substring(attrEqualsIdx + 1).Trim();
                        currentEntry.AddAttribute(attrId, attrVal, lineIndex + 1);
                    }
                    else
                    {
                        if (currentEntry.CurrentAttribute != null)
                        {
                            currentEntry.CurrentAttribute.ValueLines.Add(line.Trim());
                        }
                        else
                        {
                            currentEntry.ValueLines.Add(line.Trim());
                        }
                    }
                }
            }

            foreach (FluentEntry entry in entries)
            {
                if (entry.IsTerm)
                {
                    string termVal = string.Join(" ", entry.ValueLines).Trim();
                    terms[entry.Id] = termVal;
                }
            }

            foreach (FluentEntry entry in entries)
            {
                if (entry.IsTerm)
                {
                    continue;
                }

                string key = string.IsNullOrEmpty(keyPrefix) ? entry.Id : keyPrefix + "-" + entry.Id;
                EmitFluentEntry(entry.ValueLines, key, fileInfo, path, messages, diagnostics, terms, entry.LineNumber);

                foreach (FluentAttribute attr in entry.Attributes)
                {
                    string attrKey = key + "-" + attr.Id;
                    EmitFluentEntry(attr.ValueLines, attrKey, fileInfo, path, messages, diagnostics, terms, attr.LineNumber);
                }
            }
        }

        private static void EmitFluentEntry(
            List<string> lines,
            string key,
            LocaleFileInfo fileInfo,
            string path,
            List<RawMessage> messages,
            List<Diagnostic> diagnostics,
            Dictionary<string, string> terms,
            int line)
        {
            if (lines.Count == 0)
            {
                messages.Add(RawMessage.Simple(key, fileInfo.Locale, fileInfo.Format, Template.Parse(string.Empty, fileInfo.Format, terms), path, line));
                return;
            }

            string combined = string.Join("\n", lines);
            if (TryExtractSelectExpression(combined, out string? selectorVar, out List<(string Category, bool IsDefault, string Pattern)>? variants))
            {
                foreach (var variant in variants!)
                {
                    Template t = Template.Parse(variant.Pattern, fileInfo.Format, terms);
                    messages.Add(RawMessage.Plural(key, fileInfo.Locale, fileInfo.Format, variant.Category, false, t, path, selectorVar, line));
                }
                return;
            }

            Template template = Template.Parse(combined, fileInfo.Format, terms);
            messages.Add(RawMessage.Simple(key, fileInfo.Locale, fileInfo.Format, template, path, line));
        }

        private static bool TryExtractSelectExpression(
            string text,
            out string? selectorVar,
            out List<(string Category, bool IsDefault, string Pattern)>? variants)
        {
            selectorVar = null;
            variants = null;

            int arrowIdx = text.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx < 0)
            {
                return false;
            }

            int braceIdx = text.LastIndexOf('{', arrowIdx);
            if (braceIdx < 0)
            {
                return false;
            }

            string selectorPart = text.Substring(braceIdx + 1, arrowIdx - braceIdx - 1).Trim();
            if (selectorPart.StartsWith("$", StringComparison.Ordinal))
            {
                selectorPart = selectorPart.Substring(1).Trim();
            }

            if (string.IsNullOrEmpty(selectorPart))
            {
                return false;
            }

            string remaining = text.Substring(arrowIdx + 2);
            int closingBrace = remaining.LastIndexOf('}');
            if (closingBrace >= 0)
            {
                remaining = remaining.Substring(0, closingBrace);
            }

            List<(string Category, bool IsDefault, string Pattern)> list = new();
            string[] lines = remaining.Split('\n');
            string? currentCategory = null;
            bool currentIsDefault = false;
            List<string> currentPatternLines = new();

            void FlushCurrent()
            {
                if (currentCategory != null)
                {
                    string pattern = string.Join("\n", currentPatternLines).Trim();
                    list.Add((currentCategory, currentIsDefault, pattern));
                    currentPatternLines.Clear();
                }
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                bool isDefault = false;
                string checkLine = line;
                if (checkLine.StartsWith("*", StringComparison.Ordinal))
                {
                    isDefault = true;
                    checkLine = checkLine.Substring(1).TrimStart();
                }

                if (checkLine.StartsWith("[", StringComparison.Ordinal))
                {
                    int closeBracket = checkLine.IndexOf(']');
                    if (closeBracket > 0)
                    {
                        FlushCurrent();
                        currentCategory = checkLine.Substring(1, closeBracket - 1).Trim();
                        currentIsDefault = isDefault;
                        string restOfLine = checkLine.Substring(closeBracket + 1).Trim();
                        if (!string.IsNullOrEmpty(restOfLine))
                        {
                            currentPatternLines.Add(restOfLine);
                        }
                        continue;
                    }
                }

                if (currentCategory != null)
                {
                    currentPatternLines.Add(line);
                }
            }

            FlushCurrent();

            if (list.Count > 0)
            {
                selectorVar = selectorPart;
                variants = list;
                return true;
            }

            return false;
        }

        private sealed class FluentEntry
        {
            public FluentEntry(string id, bool isTerm, int lineNumber)
            {
                Id = id;
                IsTerm = isTerm;
                LineNumber = lineNumber;
                ValueLines = new List<string>();
                Attributes = new List<FluentAttribute>();
            }

            public string Id { get; }
            public bool IsTerm { get; }
            public int LineNumber { get; }
            public List<string> ValueLines { get; }
            public List<FluentAttribute> Attributes { get; }
            public FluentAttribute? CurrentAttribute => Attributes.Count > 0 ? Attributes[Attributes.Count - 1] : null;

            public void AddAttribute(string id, string initialVal, int lineNumber)
            {
                FluentAttribute attr = new(id, lineNumber);
                if (!string.IsNullOrEmpty(initialVal))
                {
                    attr.ValueLines.Add(initialVal);
                }
                Attributes.Add(attr);
            }
        }

        private sealed class FluentAttribute
        {
            public FluentAttribute(string id, int lineNumber)
            {
                Id = id;
                LineNumber = lineNumber;
                ValueLines = new List<string>();
            }

            public string Id { get; }
            public int LineNumber { get; }
            public List<string> ValueLines { get; }
        }
    }
}
