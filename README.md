# ZeroI18n

[![NuGet](https://img.shields.io/nuget/v/ZeroI18n.svg?style=flat-square)](https://www.nuget.org/packages/ZeroI18n)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-%3E%3D8.0-512BD4.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![NativeAOT](https://img.shields.io/badge/NativeAOT-100%25%20Ready-success.svg?style=flat-square)](#)

**ZeroI18n** is a high-performance, zero-overhead compile-time internationalization (i18n) source generator for modern .NET.

It transforms [Project Fluent](https://projectfluent.org/) (`.ftl`), ICU MessageFormat, and i18next files into strongly typed pure C# methods and switch pattern matches directly at **compile time**. **Zero reflection, zero runtime allocations, zero external runtime dependencies, and 100% NativeAOT ready.**

---

## ✨ Key Features

- ⚡ **Zero Runtime Overhead**: All localized templates are compiled into static C# switch expressions. No runtime file I/O, no JSON deserialization, and no AST interpretation.
- 🛡️ **100% NativeAOT & Trimming Friendly**: Emits pure, static C# code with string literals. Completely safe for full trimming and Ahead-Of-Time compilation.
- 📦 **Zero External Runtime Dependencies**: Installs cleanly as an analyzer dependency (`DevelopmentDependency`). Shuts down dependency conflicts by embedding its own lightweight parser.
- 🌐 **Modern Project Fluent Syntax**: Full support for variables, plural categories (`[one]`, `[other]`), selectors, attributes (`.tooltip`), and reusable terms (`-brand`).
- 🔄 **Multi-Format Support**:
  - **Project Fluent** (`.ftl`)
  - **ICU MessageFormat** (`.json`)
  - **i18next** (`.json`)
- 🎨 **Type-Safe & Reactive UI Ready**:
  - **Strongly Typed APIs**: Auto-infers method parameters (e.g. `Messages.Welcome(userName: "Alice")`).
  - **Reactive UI Support**: Emits a built-in `LocalizationManager : INotifyPropertyChanged` for seamless, live language switching in WPF, Avalonia, and .NET MAUI without app restarts.

---

## ⚡ 30-Second Quick Start

### 1. Install NuGet Package

```bash
dotnet add package ZeroI18n
```

### 2. Add Localization Files

Create a `messages/` folder in your project root and add your `.ftl` files. ZeroI18n automatically detects and includes them:

`messages/en.ftl`:
```fluent
-brand = ZeroI18n
app-name = { -brand } Demo

welcome = Welcome, { $userName }!

matched-records =
    { $count ->
        [one] Found { $count } record
       *[other] Found { $count } records
    }

login-button = Sign In
    .tooltip = Click to sign in with your account
```

`messages/zh-CN.ftl`:
```fluent
-brand = ZeroI18n
app-name = { -brand } 示例

welcome = 欢迎，{ $userName }！

matched-records =
    { $count ->
       *[other] 找到 { $count } 条记录
    }

login-button = 登录
    .tooltip = 点击以登录您的账号
```

### 3. Use in C#

Upon build, ZeroI18n automatically generates the strongly typed `Messages` class:

```csharp
using YourProject.I18n;

// 1. Strongly typed calls with parameter inference
string app = Messages.AppName();                         // "ZeroI18n Demo"
string greeting = Messages.Welcome("Alice");             // "Welcome, Alice!"
string countInfo = Messages.MatchedRecords(count: 3);    // "Found 3 records"

// Fluent attributes automatically generate composite methods
string tooltip = Messages.LoginButtonTooltip();          // "Click to sign in with your account"

// 2. Switch languages dynamically at runtime
Messages.CurrentLocale = "zh-CN";

Console.WriteLine(Messages.Welcome("Alice"));           // "欢迎，Alice！"
Console.WriteLine(Messages.LoginButton());              // "登录"
Console.WriteLine(Messages.LoginButtonTooltip());       // "点击以登录您的账号"
```

---

## 🖥️ Live UI Data Binding (WPF / Avalonia / .NET MAUI)

ZeroI18n generates an `INotifyPropertyChanged`-compliant `LocalizationManager` singleton. Changing `CurrentLocale` automatically triggers property change notifications, updating bound XAML elements instantly without restarting the application:

### XAML Binding

```xml
<TextBlock Text="{Binding [app-name], Source={x:Static i18n:LocalizationManager.Instance}}" />
<Button Content="{Binding [login-button], Source={x:Static i18n:LocalizationManager.Instance}}"
        ToolTip.Tip="{Binding [login-button.tooltip], Source={x:Static i18n:LocalizationManager.Instance}}" />
```

### Switching Language in Code / ViewModel

```csharp
// Updates CurrentLocale and refreshes all bound XAML elements automatically
LocalizationManager.Instance.CurrentLocale = "zh-CN";

// Or directly via static class:
Messages.CurrentLocale = "zh-CN";
```

---

## 📁 File Conventions & Format Detection

ZeroI18n infers the target locale from the file name (without extension). For instance, `en.ftl` maps to `en`, and `zh-CN.json` maps to `zh-CN`:

| Directory Pattern | Default Format | Description |
| :--- | :--- | :--- |
| `messages/**/*.ftl` | **Project Fluent** | Recommended; highest expressive power |
| `messages/**/*.json` | **ICU MessageFormat** | Ideal for projects migrating from ICU-based systems |
| `locales/**/*.ftl` | **Project Fluent** | Common full-stack directory structure |
| `locales/**/*.json` | **i18next** | Supports nested keys and `_other` plural conventions |

---

## ⚙️ MSBuild Configuration

You can customize the generated class name, namespace, accessibility, and fallback locale directly in your `.csproj`:

```xml
<PropertyGroup>
  <!-- Custom generated class name (Default: Messages) -->
  <ZeroI18nClassName>AppStrings</ZeroI18nClassName>

  <!-- Custom namespace (Default: <RootNamespace>.I18n) -->
  <ZeroI18nNamespace>MyCompany.MyApp.Localization</ZeroI18nNamespace>

  <!-- Access modifier: internal or public (Default: internal) -->
  <ZeroI18nAccessModifier>public</ZeroI18nAccessModifier>

  <!-- Default fallback locale (Default: en) -->
  <ZeroI18nDefaultLocale>zh-CN</ZeroI18nDefaultLocale>

  <!-- Toggle generator on/off (Default: true) -->
  <EnableZeroI18n>true</EnableZeroI18n>
</PropertyGroup>
```

---

## 🔍 Compile-Time Diagnostics

ZeroI18n performs strict syntactic validation at compile time, catching errors early during the build:

| Diagnostic ID | Severity | Description |
| :--- | :--- | :--- |
| **`ZERO001`** | Error | Invalid i18n JSON syntax or file unreadable |
| **`ZERO002`** | Warning | Unsupported i18n value type |
| **`ZERO003`** | Warning | Duplicate i18n message key |
| **`ZERO004`** | Error | Project Fluent syntax error with precise line number |

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
