## Localization (Reactive Approach)
- All internal code in English; user-facing strings via LocalizationManager
- Resource files: Strings.{culture}.resx (standard .NET .resx format)
- ResourceManager loads strings from embedded .resx resources
- Primary languages: en, ru; planned: zh-CN, ja, ko, es, de, fr
- **Runtime language switching without restart** — all UI updates automatically via reactive bindings

### Reactive Localization Pattern (MANDATORY)
- **NO INotifyPropertyChanged** for localization — use IObservable exclusively
- **NO LocalizedViewModelBase** — use direct XAML binding instead
- LocalizationManager.GetObservable(key) returns IObservable<string>
- Use Observable.Defer for lazy evaluation at subscription time
- Use ToBinding() in XAML for automatic UI updates
- Language changes propagate automatically to all {loc:Localized ...} bindings

### XAML Usage (Primary Method)
```xml
<TextBlock Text="{loc:Localized Menu.File}" />
<Button Content="{loc:Localized Order.Buy}" />
```
- LocalizedExtension returns GetObservable(Key).ToBinding()
- Updates automatically when language changes (no restart needed)
- No ViewModel properties needed for static strings
- ObserveOnUiThread() ensures updates happen on UI thread

### ViewModel Usage (ONLY for dynamic data)
```csharp
// ❌ WRONG: Don't create ViewModel properties for simple strings
public class MenuViewModel
{
    public string MenuFile { get; set; } // Unnecessary!
}

// ✅ RIGHT: Use ViewModel only for dynamic data
public class StatusBarViewModel
{
    public string ConnectionStatus { get; set; } // Dynamic - needs ViewModel
    public string CurrentTheme { get; set; } // Dynamic - needs ViewModel
}

// ❌ WRONG: Don't localize in ViewModel
Loc.GetObservable("Status.Ready")
    .Subscribe(value => StatusText = value); // Unnecessary!

// ✅ RIGHT: Localize directly in XAML
<TextBlock Text="{loc:Localized Status.Ready}" />
```

### Key Principles
- **Direct XAML binding for simple strings** — no ViewModel properties needed
- **ViewModel properties ONLY for dynamic data** — connection status, user input, etc.
- Thread-safe caching with ConcurrentDictionary
- Automatic cache invalidation on language change
- **ObserveOnUiThread()** extension method in Livery.Extensions namespace
- No manual subscription management — use DisposeWith()
- GetObservable uses Observable.Defer for lazy start value
- **Language switches on-the-fly** — all {loc:Localized} bindings update automatically
