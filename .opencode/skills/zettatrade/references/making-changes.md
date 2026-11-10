## When Making Changes
1. Always maintain backward compatibility in public interfaces
2. Add tests for new domain logic
3. Follow reactive patterns (Observable, not events)
4. Consider plugin boundary — new adapters should be separate assemblies
5. AI features should work with local models first, cloud as enhancement
6. Never hardcode exchange-specific logic in core
7. **All code, comments, and internal strings MUST be in English**
8. User-facing strings must use localization system (never hardcoded)
9. **For localization: use {loc:Localized Key} in XAML for static strings, ViewModel properties ONLY for dynamic data**
10. **NEVER create ViewModel properties for localized strings** — use direct XAML binding instead
11. Sensitive data (API keys, tokens) MUST go through ISecretStore
12. Settings changes should be reactive (IObservable<SettingsChange>)
13. New features should have corresponding config sections
14. Platform-specific code must be isolated in Infrastructure layer
15. **Use ReactiveUI exclusively** — never mix with CommunityToolkit.Mvvm
16. All ViewModels must inherit from ReactiveObject
17. Use WhenActivated for subscription lifecycle management
18. Always use ObserveOnUiThread() extension for UI updates (not ObserveOn(RxApp.MainThreadScheduler))
19. Dispose subscriptions properly with DisposeWith(disposables)
20. **Use async/await throughout** — avoid blocking calls (.Result, .Wait())
21. **NEVER use lock(_lockObject) in async code** — use SemaphoreSlim instead, even if there's no await inside the critical section
22. **ALWAYS provide a commit message in English after making changes** — brief summary (2-4 lines) describing what was changed and why, following conventional commit format (feat:, fix:, refactor:, docs:, etc.)
23. **Use #region directives** to organize large files into collapsible sections (e.g., #region Properties, #region Commands, #region Private Methods, #region Event Handlers). After #endregion, always specify the region name (e.g., #endregion Properties, #endregion Commands)
