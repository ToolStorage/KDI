# Changelog

## [2.0.0] - 2026-08-18

### Added
- `IPreUninjectable`과 `DIBehaviour.OnBeforeUninject()` 기반의 명시적인 주입 철회 단계
- 활성 구간용 `OnInjectedEnable/OnInjectedDisable`과 주입 전체 수명용 `InjectionDisposables`
- parentless additive/preview scope를 위한 `RootScopeMode.Isolated`
- activation rollback, 외부 소유권, 역순 해제를 검증하는 Unity 테스트
- Unity 컴포넌트/GameObject 파괴 시 Scope 종료를 기다리지 않고 cache·update 등록·주입을 철회하는 내부 lifetime host
- `Scope.Instantiate`로 생성한 parentless `LifetimeScope` prefab을 호출 Scope의 child로 연결하는 runtime parent 경로

### Changed
- Restricted public `IScope.Resolve` to outermost composition activation, including a fail-fast boundary around `LifetimeScope.Configure`; nested dependencies now continue only through `[Inject]` fields on the internal activation ledger
- Changed `FromFactory` and `RegisterFactory` to zero-argument factories so runtime values use closures while service dependencies keep the same field-injection syntax as every other type
- Replaced `DIBehaviour.Scope` with the narrow `DIBehaviour.Instantiator` creation/injection capability and reject resolver types as registrations, aliases, factory results, or injected fields
- Extended owned prefab creation through final `Awake`/`OnEnable` with an activation receipt that compensates committed records and newly constructed child Scopes when KDI lifecycle activation fails
- Made `DIBehaviour` injection teardown re-entry-safe and terminal for active-interval disposables created during cleanup
- Force every clone root inactive before preparation, preserve the prefab's desired `activeSelf` only for final activation, and latch staging activation or hierarchy escapes even when `PostInject` restores the final Transform state
- Keep the terminal active-subscription bucket published through synchronous destruction/revocation re-entry, and preserve the first lifecycle error when cleanup also fails
- Latch destroyed activation records and disposed child Scopes, then revalidate every receipt immediately before final activation commit
- Reject graph-building, injection, instantiation, initialization, and disposal side effects from `LifetimeScope.Configure` before they can escape Configure failure
- Updated the package dependency to `com.kylin.subscribable` 2.0.0
- Added a publish gate that refuses to release before exact Kylin package dependencies exist in the registry
- Updated trusted publishing to Node 24 and one `publish.yml` identity for both push and manual dispatch.
- `Resolve`, 단일 객체 주입, GameObject hierarchy 주입 전체를 하나의 activation transaction으로 commit하고, 실패 시 cache·필드·주입 훅·생성 객체를 실제 활성화의 역순으로 원복
- `FromInstance`는 주입은 받되 Scope가 인스턴스 자체를 Dispose하지 않는 외부 소유권으로 고정
- child Scope cascade, root registry, `LifetimeScope` 상태를 하나의 폐기 경로로 동기화
- 동적 프리팹은 목적 Scene의 비활성 staging hierarchy에서 주입을 끝낸 뒤 최종 hierarchy로 이동
- update-loop 등록/해제는 참조 동일성과 Scope별 소유 횟수를 추적하며, 마지막 해제 요청 즉시 이후 callback에서 제외. 주입 철회 뒤 수동 callback이 남거나 rollback 불가능한 외부 변경이 생기는 것을 막기 위해 같은 identity의 public 수동 등록과 Scope-managed/direct-injected 소유권 혼합 및 Configure/transaction/final Unity activation 중 수동 등록/해제는 fail-fast
- `IInjectable`/`IDisposable` Transient를 activation order에 보관해 주입 철회와 Dispose를 Scope 종료 시 보장
- GameObject 파괴 전 수동 해제된 `LifetimeScope`와 parent cascade child를 비활성화하고, 재초기화 시 주입 완료 후 다시 활성화
- `LifetimeScope.Initialize()` 실패와 잘못 숨겨진 `Awake/OnDestroy` hierarchy를 즉시 비활성화하고, 명시적 재시도 성공 뒤에만 재활성화
- factory가 같은 `IDisposable` identity를 여러 Scope에 반환하면 fail-fast하며, 같은 Scope의 다중 binding은 최초 activation만 소유
- update priority getter 예외와 Unity fake-null update 대상은 해당 객체 단위로 격리
- rollback cleanup 중 새 Resolve를 거부하고 Unity 파괴 callback은 transaction record를 tombstone 처리해 nested savepoint를 보존
- public direct injection도 필드 Resolve 전에 external identity로 고정하며, ambient sibling transaction은 소유권을 공유하지 않음
- 모든 필드 resolve 직후와 `PostInject`/최초 `OnInjectedEnable` 뒤 Unity target/dependency를 재검사해, 뒤쪽 factory가 target을 파괴한 경우에도 lifecycle callback이 invalid graph를 관찰하거나 partial graph가 commit되지 않도록 차단
- SubsystemRegistration에서 수동 생성 Scope를 포함한 모든 live concrete Scope를 dispose하고, 한 cleanup 단계가 실패해도 나머지 정적 상태 reset을 계속하며, 생존 external identity의 약한 소유권 표식은 유지
- cached Unity 서비스의 예상 밖 파괴는 이미 주입된 소비자까지 철회하도록 owning Scope 전체를 fail-closed 종료
- 모든 `FromInstance` identity를 activation 전에 선행 관찰해 factory와의 등록 순서가 외부 소유권을 바꾸지 않도록 고정
- callback이 없는 Component/ScriptableObject 서비스와 direct injection target의 파괴도 전역 lifetime monitor가 감지하며, managed update callback 직전에는 owning Scope와 ancestor의 hostless cache를 다시 검사해 같은 phase의 앞 callback이 파괴한 전이 dependency도 뒤 callback이 관찰하지 못하게 차단
- `UpdateLoopManager`가 예기치 않게 파괴되어도 update 등록, 수동/Scope별 ref-count, retire 상태와 Unity lifetime monitor를 새 manager로 이전하고 lease monitor를 재연결
- hostless Unity 서비스/direct injection target이나 먼저 주입된 sibling/owned clone이 activation 중 파괴되면 savepoint-aware transaction 경계 검증이 성공 반환 전에 partial graph를 rollback

### Breaking
- `FromFactory`/`RegisterFactory` no longer receive `IScope`; capture non-DI runtime values and declare service dependencies with `[Inject]`
- `DIBehaviour.Scope` was removed; use `Instantiator` for dynamic Unity object creation/injection
- Public nested `Resolve` from factories, `PostInject`, or other activation callbacks now throws
- `DIBehaviour.OnEnable`, `OnDisable`, `Dispose`의 override를 제거했다. 파생 클래스는 전용 `OnInjectedEnable/OnInjectedDisable/OnBeforeUninject` 훅을 사용해야 한다.
- `PostInject`가 시작된 뒤 실패해도 `PreUninject`가 호출될 수 있다. 구현은 부분 초기화 상태를 허용하고 idempotent해야 한다.
- parentless primary `LifetimeScope`는 동시에 하나만 허용된다. additive/preview root는 Inspector에서 `Isolated`로 지정해야 한다.
- `Scope.Instantiate`로 만든 active prefab은 주입 완료 후 `Awake/OnEnable`이 실행된다.
- active prefab을 inactive parent 아래에 만드는 호출은 실패한다. active prefab도 `PostInject` 중 clone root는 `false`이며, callback 종료 시 root를 `true`로 남기거나 staging을 실제 활성화하거나 clone을 staging 밖으로 이동한 코드는 실패한다. 비활성 staging 내부의 관찰 불가능한 root `true → false` 왕복은 lifecycle을 실행하지 않도록 격리되지만, callback이 만든 임의의 외부 side effect까지 rollback되지는 않는다. 처음부터 inactive인 prefab의 이후 수동 활성화는 caller-owned lifecycle이다.
- 모든 `IInjectable`은 한 번에 하나의 Scope만 소유할 수 있다. 같은 Scope의 재주입은 no-op이고, 다른 Scope의 재주입은 기존 lease 철회 전까지 실패한다.
- 성공한 `IInjectable`/`IDisposable` Transient는 caller-owned가 아니라 Scope-owned다. 호출자가 별도로 Dispose하면 안 된다.
- `LifetimeScope.Awake/OnDestroy` override를 제거했다. 파생 Scope는 `Configure`를 사용하고 Unity lifecycle message를 선언하지 않아야 한다.
- `Scope.Instantiate`는 factory, `PostInject`, 최초 `OnInjectedEnable` 등 activation transaction 내부에서 fail-fast한다. graph commit 후 명시적인 runtime 단계에서 생성해야 한다.
- `Scope.Instantiate` 결과는 호출 Scope가 소유하며 Scope 종료 시 비활성화 후 파괴된다. Scope보다 오래 살아야 하는 객체는 직접 생성하고 external-owned `InjectGameObject`를 사용해야 한다.
- custom `IScope` 주입과 custom parent Scope는 lifetime ledger가 없어 원자적 rollback/철회를 보장할 수 있으므로 지원하지 않고 fail-fast한다.
- Transient `UnityEngine.Object` 서비스는 외부 파괴 시 기존 소비자를 안전하게 철회할 dependency edge가 없으므로 Resolve 단계에서 거부한다. Scoped/Singleton 서비스 또는 external-owned GameObject 경계를 사용해야 한다.

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
