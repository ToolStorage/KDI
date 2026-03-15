# Changelog

## [1.0.0] - 2026-03-15

### Added
- Scope-based DI container with hierarchical parent-child resolution
- Field injection via `[Inject]` attribute
- Lifetime management: Singleton, Scoped, Transient
- `LifetimeScope` MonoBehaviour for Unity scene integration
- `DIBehaviour` base class with Push injection and CompositeDisposable
- Dynamic object creation: `scope.Instantiate()`, `scope.InjectGameObject()`
- Managed update loop: `IUpdatable`, `IFixedUpdatable`, `ILateUpdatable`
- SubscribableProperty reactive system with LINQ extensions
- SubscribableCollection and SubscribableDictionary
- Closure Profiler and Subscribable Property Debugger editor tools
- All-or-nothing injection (partial injection prevention)
- Warning for `[Inject]` fields on types missing `IInjectable`
