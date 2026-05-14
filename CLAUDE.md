# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

KDI (Kylin Dependency Injection) is a Unity 6 package (`com.kylin.di`) — a minimal scope-based DI framework with field-only injection and built-in reactive properties. Core DI logic is ~500 lines. Assembly name: `Kylin.DI`, root namespace: `Kylin`.

This is a Unity Package Manager (UPM) package, not a standalone project. There are no build/test/lint commands to run directly — it compiles within a Unity project that references it.

## Architecture

### DI Pipeline (Runtime/DI/)

The injection flow is: `LifetimeScope.Configure()` → `ScopeBuilder.Build()` → `Scope` created → `LifetimeScope.InjectChildren()` pushes `[Inject]` fields into child transforms.

- **KDI** — Static facade. Holds the `RootScope` reference. Auto-resets via `[RuntimeInitializeOnLoadMethod]`.
- **ScopeBuilder** — Collects `Registration` entries via fluent `Bind<T>().To<TImpl>().AsScoped()` API. Freezes after `Build()`. Enforces Singleton-only-in-RootScope rule at build time.
- **DependencyBuilder<T>** — Fluent builder returned by `ScopeBuilder.Bind<T>()`. Lifetime chain terminates with `AsSingleton()`/`AsScoped()`/`AsTransient()`/`FromInstance()`.
- **Scope** — Implements `IScope`. Holds `Dictionary<Type, Registration>` (frozen) and `Dictionary<Type, object>` (cached instances). Resolution walks parent chain. Circular reference detection uses `[ThreadStatic] HashSet<Type>`. Dispose cascades children → instances (IDisposable + UpdateLoopManager unregister).
- **Registration** — Data class: ServiceType, ImplementationType, Instance, Lifetime, Factory.
- **DependencyInjector** — Static. Two-phase inject: resolve all fields first, then set all at once (atomic). `ConcurrentDictionary<Type, FieldInfo[]>` cache walks inheritance up to MonoBehaviour/object. Warns on `[Inject]` fields without `IInjectable`.
- **InstanceFactory** — `Expression.Lambda.Compile()` cached factory for parameterless constructors.
- **ScopeExtensions** — `IScope.Instantiate()` and `IScope.InjectGameObject()` for runtime prefab spawning with DI.

### Unity Integration (Runtime/Core/)

- **LifetimeScope** — Abstract MonoBehaviour. Subclass and override `Configure(ScopeBuilder)`. Parent set via Inspector `_parent` field. Auto-initializes in `Awake` (configurable). Static registry uses `Transform.IsChildOf` (native) instead of `GetComponentInParent` (managed) for scope lookup. Push injection stops at child LifetimeScope boundaries.
- **DIBehaviour** — Abstract MonoBehaviour implementing `IInjectable`. Provides `_cd` (CompositeDisposable) for subscription cleanup on `OnDisable`, and `Scope` property for runtime access.

### Key Marker Interfaces

- `IDependencyObject` — Required on types registered via `To<T>()` or `FromInstance()`.
- `IInjectable` — Required for `[Inject]` field injection to work. Without it, fields are silently skipped (with a warning).
- `IPostInjectable` — Optional. `PostInject()` called after all `[Inject]` fields are set.

### Reactive System (Runtime/SubscribableProperty/)

`SubscribableProperty<T>` — Observable value with `Subscribe(callback, invokeInitial)` → `IDisposable`. Uses `EqualityComparer<T>.Default` for change detection. Supports Unity serialization (`ISerializationCallbackReceiver`). External serialization (MessagePack, JSON etc.) is supported via separate adapter packages (`com.kylin.di.messagepack`). Namespace: `Kylin.SubscribableProperty`.

Also includes: `SubscribableCollection<T>`, `SubscribableDictionary<TKey,TValue>`, `SubscribableCommand`, and LINQ extensions (`Select`, etc.).

### Update Loop (Runtime/Update/)

`UpdateLoopManager` — Singleton MonoBehaviour (`DontDestroyOnLoad`). Non-MonoBehaviour classes implementing `IUpdatable`/`IFixedUpdatable`/`ILateUpdatable` are auto-registered when resolved through a Scope. Supports `IUpdatePriority` (lower value = earlier execution). Registration/unregistration is deferred via a pending queue processed each frame.

Interface method names are prefixed: `KDIUpdate`, `KDIFixedUpdate`, `KDILateUpdate`.

## Design Constraints

- **Field injection only** — No constructor injection. All DI-managed types must have a public parameterless constructor.
- **Singleton only in RootScope** — `AsSingleton()` in a child scope throws at build time.
- **Push injection model** — LifetimeScope walks child transforms; components don't pull their own dependencies.
- **Scope freeze** — `ScopeBuilder` cannot accept registrations after `Build()`.

## Language

Source code comments and log messages are in Korean. Follow this convention when modifying existing code.
