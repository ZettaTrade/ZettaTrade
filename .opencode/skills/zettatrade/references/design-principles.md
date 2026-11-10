## Design Principles (MANDATORY)

### SOLID Principles

**S - Single Responsibility Principle (SRP)**
- Each class should have ONE reason to change
- ViewModels should coordinate, not contain business logic
- Split large ViewModels into focused sub-ViewModels (MenuViewModel, StatusBarViewModel, ChatViewModel, SettingsViewModel)
- Services should do one thing well

**O - Open/Closed Principle (OCP)**
- Open for extension, closed for modification
- Use interfaces and dependency injection for extensibility
- Prefer composition over inheritance
- Use strategy pattern for varying behaviors

**L - Liskov Substitution Principle (LSP)**
- Derived classes must be substitutable for their base classes
- Don't override methods to throw exceptions or change behavior
- Use interfaces for contracts, not inheritance hierarchies

**I - Interface Segregation Principle (ISP)**
- Many small interfaces > one large interface
- Clients should not depend on methods they don't use
- Split IExchangeAdapter into IMarketDataAdapter and ITradingAdapter
- Create role-specific interfaces (IReadable, IWritable, INotifiable)

**D - Dependency Inversion Principle (DIP)**
- Depend on abstractions, not concretions
- Inject dependencies through constructors
- Use interfaces for all external dependencies
- Never use new keyword for services — use DI container

### DRY (Don't Repeat Yourself)
- **NEVER duplicate code** — extract to methods, classes, or helpers
- Use extension methods for repeated operations
- Centralize configuration in appsettings.json
- Delegate to managers (ThemeManager, LocalizationManager) instead of duplicating data

### KISS (Keep It Simple, Stupid)
- **Avoid over-engineering** — solve the actual problem, not hypothetical ones
- Prefer simple solutions over clever ones
- Readable code > clever code
- Don't add features "just in case"
- Delete code that is no longer needed

### YAGNI (You Aren't Gonna Need It)
- **Don't implement until you need it**
- No speculative features or abstractions
- Remove TODO placeholders if not implementing soon
- Start with minimal viable implementation, iterate later

### Composition Over Inheritance
- **Prefer composition** — build complex objects from simple parts
- Use dependency injection to compose services
- Inheritance only for "is-a" relationships, not "has-a"
- Favor interfaces over abstract classes

### Law of Demeter (Principle of Least Knowledge)
- Objects should only talk to their immediate friends
- Don't chain property access: a.B.C.D.Method()
- Pass only what is needed, not entire objects
- Use facade pattern to simplify complex APIs

### Tell, Don't Ask
- **Tell objects what to do**, don't ask for their state
- Bad: if (order.Status == Pending) order.Cancel()
- Good: order.TryCancel()
- Encapsulate behavior in domain objects
- Avoid anemic domain models

### Principle of Least Astonishment
- Code should do what its name suggests
- Method names should describe behavior, not implementation
- Avoid side effects in getters
- Follow established conventions and patterns

### Separation of Concerns (SoC)
- **Separate UI, business logic, and data access**
- ViewModels = UI coordination only
- Services = business logic
- Repositories = data access
- Never mix concerns in one class

### Single Level of Abstraction (SLAP)
- Each method should operate at ONE level of abstraction
- High-level methods orchestrate, low-level methods implement
- Don't mix SaveOrder() with ValidateEmailFormat()
- Extract helper methods for different abstraction levels

### ViewModel Architecture Example
- **MainViewModel** - Coordinator only, delegates to sub-ViewModels
- **MenuViewModel** - Menu localization and commands
- **StatusBarViewModel** - Status bar (connection, theme, language)
- **ChatViewModel** - AI chat functionality
- **SettingsViewModel** - Settings management (theme, language)
- Each ViewModel has single responsibility and is independently testable
