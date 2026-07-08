# Changelog

## [1.4.0] - 2026-07-08

### Changed
- 인스턴스 생성 방식을 `Expression.Lambda.Compile()`에서 **등록 시점 캡처 델리게이트**로 교체. `To<TImpl>()`은 `() => new TImpl()`을 정적으로 캡처하고, `ToSelf()`는 파라미터 없는 생성자를 등록 시점에 검증한 뒤 `Activator.CreateInstance`로 폴백한다. 표현식 컴파일(JIT)·`System.Linq.Expressions` 의존성이 제거되어 **IL2CPP(AOT) 환경에서 안정적이고 코드 스트리핑 영향을 받지 않는다** (모바일 빌드 대응). `InstanceFactory` 클래스 삭제
- `Bind<T>().To<TImpl>()`에 **`new()` 제약 추가** — 파라미터 없는 public 생성자 누락이 런타임 `Resolve` 예외 대신 **컴파일 타임 에러**로 드러난다. 생성자 인자가 필요한 타입은 종전대로 `FromInstance()`/`FromFactory()`로 등록 (필드 주입 설계상 이미 지켜지던 계약이므로 정상 코드에는 영향 없음)

## [1.3.0] - 2026-07-07

### Added
- `Bind<T>().AsEntryPoint()` — 스코프 빌드 시점에 즉시 인스턴스화. lazy resolve로 인해 "아무도 주입하지 않으면 생성되지 않던" 시스템 서비스(IUpdatable 시뮬레이션 등)를 확실히 기동. 생성 시 `[Inject]` 주입 + `IPostInjectable.PostInject()`가 함께 실행되며, 의존성 순서는 resolve가 자연히 처리 (별도 IStartable 인터페이스 없음)
- `Bind<T>().To<TImpl>().AlsoBind<TOther>()` — 하나의 단일 인스턴스를 여러 인터페이스로 노출. 기존에는 같은 구현을 두 번 `To`로 등록하면 인스턴스가 2개 생겨 상태가 분열되던 footgun을 해소. 별칭 타입이 구현체와 맞지 않으면 빌드 타임 에러(팩토리는 생성 시점 검증)

## [1.2.0] - 2026-07-07

### Added
- `IInstantiator` — Resolve 권한 없는 동적 생성 전용 인터페이스. 모든 Scope에 자동 등록되어 `[Inject]`로 주입 가능. 팩토리 클래스에 IScope를 넘기던 보일러플레이트 제거
- `Bind<T>().ToSelf()` — 구체 타입 자기 바인딩 축약
- Build 타임 검증 강화 (fail-fast):
  - 종결 메서드 없이 끝난 fluent 체인 (`Bind().To()`까지만 쓰고 `.AsScoped()` 누락 등) → Build 에러
  - 같은 스코프 내 중복 등록 → 에러 (오버라이드는 자식 스코프에서)
  - Transient + IUpdatable 계열 조합 → 에러 (스코프가 수명을 추적하지 못해 해제 불가)
  - Transient + IDisposable 조합 → 경고 (Dispose는 생성한 쪽 책임)
- Scope에 진단용 이름 부여 (LifetimeScope 타입명) — Resolve 실패/순환참조 메시지에 스코프 체인 표시 (`BattleScope → AppRootScope 체인에서 ...`)
- LifetimeScope 커스텀 인스펙터 — Play 모드에서 등록 목록·Lifetime·Resolve 상태 표시 (Editor 어셈블리 신설)
- `Samples~/UnitSpawn` — 등록/주입/동적 생성 기본 샘플

### Changed
- `FromInstance`/`RegisterInstance` 인스턴스는 Build() 시점에 즉시 `[Inject]` 주입 + Update 루프 등록 (기존: 주입/등록되지 않았음)
- LifetimeScope가 자기 GameObject에 붙은 IInjectable 컴포넌트도 주입 (기존: 하위 Transform만 순회하여 스코프 GO 자신의 컴포넌트는 누락)
- 순환참조 메시지의 타입 체인이 실제 Resolve 순서를 정확히 표시 (HashSet → List)

### Fixed
- 부모 Scope로 위임되는 Resolve가 순환참조로 오탐지되던 문제 (진입점에서만 추적하도록 수정)
- Transient IUpdatable이 스코프 파괴 후에도 Update 루프에 남아 영구 호출되던 누수 (조합 자체를 빌드 타임에 차단)
- 씬 종료 중 Scope Dispose가 UpdateLoopManager GameObject를 새로 생성할 수 있던 문제 (non-creating 접근자 사용)

### Removed (Breaking)
- `KDI.RootScope` public 접근 제거 (internal화) — 전역 Resolve 진입점(서비스 로케이터) 차단
- 파라미터 없는 `IInjectable.Inject()` 확장 메서드 제거 — 주입은 항상 만드는 쪽이 스코프를 지정해 수행
- 미사용 `ViewModelAttribute` 제거

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
