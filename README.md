# KDI (Kylin Dependency Injection)

Unity 6 전용 Scope 기반 경량 DI 프레임워크. 필드 주입 전용, 계층적 Scope, 반응형 프로퍼티 내장.

```
com.kylin.di | Unity 6000.0+ | MIT License
```

Version compatibility:

- KDI 2.0.0 requires `com.kylin.subscribable` 2.0.0.
- KDI Layered 2.0.0 targets this KDI 2.0.0 contract.
- KDI MessagePack Adapter 2.0.0 can be added when Subscribable values need MessagePack serialization.

---

## 목차

- [설치](#설치)
- [핵심 개념](#핵심-개념)
- [기본 사용법](#기본-사용법)
  - [1. 서비스 정의](#1-서비스-정의)
  - [2. LifetimeScope에 등록](#2-lifetimescope에-등록)
  - [3. MonoBehaviour에서 사용](#3-monobehaviour에서-사용)
- [Scope 계층 구성](#scope-계층-구성)
  - [씬 하이어라키 구조](#씬-하이어라키-구조)
  - [부모-자식 Scope 연결](#부모-자식-scope-연결)
  - [Resolution 우선순위](#resolution-우선순위)
- [등록 API](#등록-api)
  - [Fluent Binding](#fluent-binding)
  - [Lifetime 규칙](#lifetime-규칙)
  - [팩토리 등록](#팩토리-등록)
- [동적 객체 생성](#동적-객체-생성)
- [Update Loop 시스템](#update-loop-시스템)
- [SubscribableProperty (반응형 프로퍼티)](#subscribableproperty-반응형-프로퍼티)
- [디버그 도구](#디버그-도구)
- [상용 DI 프레임워크와의 비교](#상용-di-프레임워크와의-비교)

---

## 설치

### Scoped Registry (권장)

`Packages/manifest.json`에 Kylin registry와 정확한 패키지 버전을 추가한다:

```json
{
  "scopedRegistries": [
    {
      "name": "Kylin",
      "url": "https://registry.npmjs.org",
      "scopes": ["com.kylin"]
    }
  ],
  "dependencies": {
    "com.kylin.di": "2.0.0"
  }
}
```

이 방식은 정확히 호환되는 `com.kylin.subscribable` 2.0.0을 전이 의존성으로 함께 설치한다.

### Git URL

registry를 사용하지 않는 프로젝트는 두 패키지를 동일한 릴리스 태그로 직접 고정한다:

```json
{
  "dependencies": {
    "com.kylin.subscribable": "https://github.com/ToolStorage/KDI-Subscribable.git#v2.0.0",
    "com.kylin.di": "https://github.com/ToolStorage/KDI.git#v2.0.0"
  }
}
```

기본 사용 예제는 Package Manager → KDI → Samples → **Unit Spawn**에서 임포트할 수 있다.

---

## 핵심 개념

KDI는 세 가지 마커 인터페이스로 동작한다:

| 인터페이스 | 역할 | 필수 여부 |
|-----------|------|----------|
| `IDependencyObject` | DI 컨테이너에 등록 가능한 타입 표시 | `To<T>()`, `FromInstance()` 사용 시 필수 |
| `IInjectable` | `[Inject]` 필드 주입 대상 표시 | 필드 주입을 받으려면 필수 |
| `IPostInjectable` | 주입 완료 후 `PostInject()` 콜백 | 선택 |

`IInjectable` 없이 `[Inject]` 필드를 선언하면 **주입되지 않고 경고만 출력된다.** 이는 의도적 설계로, 주입 대상을 명시적으로 표시하도록 강제한다.

하나의 `IInjectable` 인스턴스에는 동시에 하나의 Scope만 주입할 수 있다. 같은 Scope로 다시 주입하는 호출은 no-op이며, 다른 Scope는 기존 Scope가 lease를 철회하기 전까지 fail-fast한다. 이는 공유 `FromInstance`의 필드와 cleanup 상태가 두 Scope 사이에서 덮이는 것을 막는다. `FromInstance`와 외부 객체에 대한 public `Inject`는 객체 identity를 external-owned로 고정하며, lease가 끝난 뒤에도 factory-owned로 이전되지 않는다.

---

## 기본 사용법

### 1. 서비스 정의

```csharp
// 인터페이스 — IDependencyObject를 상속
public interface IScoreService : IDependencyObject
{
    SubscribableProperty<int> Score { get; }
    void AddScore(int amount);
}

// 구현체 — IInjectable로 필드 주입 활성화
public class ScoreService : IScoreService, IInjectable
{
    [Inject] private IGameConfig _config;

    public SubscribableProperty<int> Score { get; } = new(0);

    public void AddScore(int amount)
    {
        Score.Value += amount * _config.ScoreMultiplier;
    }
}
```

주입 완료 후 초기화가 필요하면 `IPostInjectable`을 추가한다:

```csharp
public class BattleService : IDependencyObject, IInjectable, IPostInjectable
{
    [Inject] private IUnitRepository _unitRepo;
    [Inject] private IMapService _mapService;

    private BattleState _state;

    public void PostInject()
    {
        // 이 시점에서 모든 [Inject] 필드가 주입 완료됨
        _state = new BattleState(_unitRepo.GetAllUnits(), _mapService.CurrentMap);
    }
}
```

### 2. LifetimeScope에 등록

`LifetimeScope`를 상속하고 `Configure` 메서드에서 서비스를 등록한다:

```csharp
public class GameSceneScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IGameConfig>().To<GameConfig>().AsScoped();
        builder.Bind<IScoreService>().To<ScoreService>().AsScoped();
    }
}
```

이 컴포넌트를 씬의 GameObject에 추가하면, `Awake` 시 자동으로 Scope가 빌드되고 **하위 Transform의 모든 `IInjectable` 컴포넌트에 주입이 실행된다** (Push 주입).

### 3. MonoBehaviour에서 사용

`DIBehaviour`를 상속하면 `[Inject]` 필드 주입과 구독 수명 관리를 모두 받는다:

```csharp
public class ScoreUI : DIBehaviour
{
    [Inject] private IScoreService _scoreService;

    [SerializeField] private TMP_Text _scoreText;

    protected override void OnInjectedEnable()
    {
        _scoreService.Score
            .Subscribe(score => _scoreText.text = $"Score: {score}", invokeInitial: true)
            .AddTo(_cd);  // 비활성화 시 해제되고 재활성화 시 다시 구성
    }
}
```

`DIBehaviour`가 제공하는 것:

- `[Inject]` 필드 자동 주입 (`IInjectable` 구현 내장)
- `OnInjectedEnable/OnInjectedDisable` — 활성 구간의 시작과 종료
- `_cd` (`CompositeDisposable`) — 비활성화 시 모든 활성 구간 구독 정리
- `InjectionDisposables` — Scope가 주입을 철회할 때까지 유지할 자원
- `OnBeforeUninject` — 의존성 필드가 원래 값으로 복원되기 직전의 정리 훅
- `Instantiator` 프로퍼티 — Resolve 권한 없이 현재 Scope가 소유하는 Unity 객체 생성/주입

파생 클래스는 Unity의 `OnEnable/OnDisable`을 선언하지 않는다. 선언하면 주입 시 fail-fast하며, 활성 수명 로직은 위 전용 훅으로 통일한다.

---

## Scope 계층 구성

### 씬 하이어라키 구조

KDI의 Scope는 Unity 하이어라키와 1:1로 대응된다. **LifetimeScope 컴포넌트가 붙은 GameObject의 하위 Transform이 해당 Scope의 주입 영역**이다.

```
씬 하이어라키                           Scope 구조
─────────────                         ──────────
[RootScope]     ← LifetimeScope       RootScope (Singleton 등록)
  ├── GlobalUI                          │
  └── [BattleScope]  ← LifetimeScope    └── BattleScope (Scoped 등록)
        ├── Player                            │
        │     └── HealthBar (DIBehaviour)     ├── HealthBar에 주입
        ├── EnemySpawner (DIBehaviour)        ├── EnemySpawner에 주입
        └── [UIScope]  ← LifetimeScope        └── UIScope (별도 Scope)
              └── DamagePopup (DIBehaviour)         └── DamagePopup에 주입
```

**핵심 규칙**: LifetimeScope가 하위 Transform을 순회하며 주입할 때, **다른 LifetimeScope를 만나면 탐색을 중단**한다. UIScope 아래의 컴포넌트는 BattleScope가 아닌 UIScope에서 주입받는다.

### 부모-자식 Scope 연결

Inspector에서 `_parent` 필드를 지정하여 Scope 계층을 구성한다:

```csharp
// Root — parent 없음 → RootScope로 자동 설정
public class AppRootScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<ILogger>().To<GameLogger>().AsSingleton();
        builder.Bind<IAudioService>().To<AudioService>().AsSingleton();
    }
}

// Child — Inspector에서 _parent = AppRootScope 지정
public class BattleSceneScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IBattleService>().To<BattleService>().AsScoped();
        builder.Bind<IUnitManager>().To<UnitManager>().AsScoped();
    }
}
```

`_parent`가 `null`이고 Root Mode가 `Primary`인 LifetimeScope는 **RootScope**로 동작한다. Primary root는 동시에 하나만 허용된다. additive scene, prefab preview처럼 독립 root가 필요한 경우 Root Mode를 `Isolated`로 지정한다. 프레임워크 내부 RootScope를 외부에서 직접 Resolve하는 API는 제공하지 않는다.

`_autoInitialize`(기본값 `true`)를 `false`로 설정하면 `Awake`에서 자동 초기화하지 않고, 수동으로 `Initialize()`를 호출해야 한다. parent가 아직 초기화되지 않은 경우 자동으로 parent를 먼저 초기화한다.

`LifetimeScope` 파생 클래스는 `Awake/OnDestroy`를 선언하지 않는다. 이 Unity message는 Scope 생성·해제를 보장하는 framework lifecycle이며, 초기 구성은 `Configure`로 통일한다. GameObject 파괴 없이 `Dispose()`하거나 `Initialize()`가 실패하면 해당 Scope GameObject와 cascade된 활성 child Scope가 의존성 없는 상태로 실행되지 않도록 비활성화된다. 실패 원인을 고친 뒤 `Initialize()`를 명시적으로 다시 호출하면 비활성 상태에서 재주입을 끝낸 뒤 이전 활성 상태로 복구된다. 파생 클래스가 framework message를 숨긴 경우에도 scene-load 검증이 해당 hierarchy만 격리하고 나머지 Scope 검증을 계속한다.

### Resolution 우선순위

Resolve 요청은 **현재 Scope → 부모 Scope → ... → RootScope** 순으로 탐색한다:

Public `IScope.Resolve`는 테스트 bootstrap이나 명시적인 composition root에서 graph activation을 시작하는 outermost 호출에만 사용한다. `LifetimeScope.Configure`, factory, `PostInject`, 다른 activation callback 안에서 다시 호출하면 fail-fast한다. 중첩 의존성은 오직 `[Inject]` 필드가 내부 activation ledger를 이어서 해석한다. `Configure`에는 등록 선언과 DI가 아닌 외부 생성값만 전달하며, 이미 존재하는 다른 Scope를 서비스 로케이터처럼 사용하지 않는다.

```
BattleScope에서 Resolve<ILogger>() 호출 시:

1. BattleScope에 ILogger 인스턴스가 캐싱되어 있는가?    → 없음
2. BattleScope에 ILogger 등록(Registration)이 있는가?  → 없음
3. Parent(RootScope)에게 위임
4. RootScope에 ILogger 등록이 있는가?                  → 있음! → 반환
```

**부모와 자식에 같은 인터페이스가 등록된 경우, 자식 Scope의 등록이 우선한다.** 이는 Scope 체인 탐색이 현재 Scope부터 시작하기 때문이다. 부모까지 올라가기 전에 자식에서 이미 찾기 때문에, 자식 Scope에서 부모의 서비스를 **오버라이드**할 수 있다.

```csharp
// RootScope: 기본 구현 등록
public class AppRootScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IDamageCalculator>().To<DefaultDamageCalculator>().AsSingleton();
    }
}

// BattleScope: 전투 전용 구현으로 오버라이드
public class BattleSceneScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        // BattleScope 하위에서 Resolve<IDamageCalculator>() 시
        // → BossDamageCalculator가 반환됨 (자식 우선)
        builder.Bind<IDamageCalculator>().To<BossDamageCalculator>().AsScoped();
    }
}
```

이 패턴을 활용하면:
- **테스트**: 테스트용 Scope에서 Mock 구현으로 오버라이드
- **씬별 특화**: 같은 인터페이스의 씬 특화 구현 등록
- **기능 전환**: 특정 구간에서만 다른 동작 적용

---

## 등록 API

### Fluent Binding

`Configure` 메서드 내에서 `ScopeBuilder`의 Fluent API를 사용한다:

```csharp
protected override void Configure(ScopeBuilder builder)
{
    // 인터페이스 → 구현체 바인딩
    builder.Bind<IService>().To<ServiceImpl>().AsScoped();
    builder.Bind<IService>().To<ServiceImpl>().AsSingleton();   // RootScope에서만
    builder.Bind<IService>().To<ServiceImpl>().AsTransient();

    // 자기 바인딩 — 구체 타입 그대로 주입받을 때 (예: KDILayered [OwnerOnly] 패턴)
    builder.Bind<PlayerData>().ToSelf().AsScoped();

    // 기존 인스턴스 등록 (항상 Scoped 취급, Build 시점에 즉시 주입됨)
    builder.Bind<IService>().FromInstance(existingInstance);

    // 팩토리 등록 — DI가 아닌 runtime 생성값이 필요할 때
    builder.Bind<IService>()
           .FromFactory(() => new ServiceImpl(runtimeOptions))
           .AsScoped();

    // 다중 인터페이스 → 단일 인스턴스 (AlsoBind)
    builder.Bind<IPlayerService>().To<PlayerService>()
           .AlsoBind<IDamageReceiver>().AsScoped();

    // 엔트리포인트 — 아무도 주입하지 않아도 빌드 시점에 즉시 기동
    builder.Bind<GameSimulation>().ToSelf().AsEntryPoint().AsScoped();
}
```

`To<T>()`의 타입 제약: `T`는 반드시 `IDependencyObject`와 바인딩 인터페이스를 동시에 구현해야 한다.

### AlsoBind — 하나의 인스턴스를 여러 인터페이스로

한 구현체를 여러 인터페이스로 노출해야 할 때, 각각 `To`로 등록하면 **인스턴스가 인터페이스 수만큼 생겨** 상태가 분열된다. `AlsoBind`는 이들을 **동일한 단일 인스턴스**로 묶는다:

```csharp
// PlayerService : IPlayerService, IDamageReceiver
builder.Bind<IPlayerService>().To<PlayerService>()
       .AlsoBind<IDamageReceiver>().AsScoped();

// IPlayerService, IDamageReceiver 어느 쪽으로 resolve하든 같은 PlayerService
```

구현체가 별칭 인터페이스를 구현하지 않으면 Build 타임 에러(팩토리 등록은 생성 시점 검증).

### AsEntryPoint — 지연 생성 우회

KDI는 lazy resolve이므로, **아무 곳에서도 주입받지 않는 서비스는 생성되지 않는다.** `IUpdatable` 시뮬레이션처럼 스스로 돌아야 하는 시스템 서비스가 이 함정에 빠진다(등록했는데 `KDIUpdate`가 안 불림). `AsEntryPoint()`는 스코프 빌드 시점에 즉시 인스턴스화한다:

```csharp
public class GameSimulation : IDependencyObject, IInjectable, IUpdatable
{
    [Inject] private IGameState _state;
    public void KDIUpdate(float dt) => _state.Tick(dt);
}

// 아무도 GameSimulation을 [Inject]하지 않아도 빌드 시 생성 + Update 루프 등록
builder.Bind<GameSimulation>().ToSelf().AsEntryPoint().AsScoped();
```

생성 시 `[Inject]` 주입과 `IPostInjectable.PostInject()`가 함께 실행된다. 엔트리포인트가 서로 의존하면 resolve 순서가 자연히 의존성 순서를 따른다(A가 B를 주입하면 B가 먼저 생성). Transient는 eager 생성해도 캐시되지 않아 유실되므로 `AsEntryPoint`와 함께 쓸 수 없다(Build 에러).

**Build 타임 검증** — 아래 실수는 조용히 넘어가지 않고 `Build()`(= LifetimeScope 초기화) 시점에 즉시 에러가 된다:

| 실수 | 결과 |
|------|------|
| `.AsScoped()` 등 종결 메서드 누락 (`Bind().To()`까지만 작성) | Build 에러 |
| 같은 스코프에 같은 서비스 타입 중복 등록 | Build 에러 (오버라이드는 자식 스코프에서) |
| child scope에 `AsSingleton()` 등록 | Build 에러 |
| Transient + `IUpdatable` 계열 조합 | Build 에러 (아래 Lifetime 규칙 참고) |
| Transient + `UnityEngine.Object` 조합 | Resolve 에러 (파괴 시 기존 소비자를 안전하게 철회할 수 없음) |
| Transient + `IDisposable` 조합 | Scope 종료까지 추적 후 역순 Dispose (대량 생성은 짧은 child Scope 권장) |

### Lifetime 규칙

| Lifetime | 동작 | 등록 위치 |
|----------|------|-----------|
| `AsSingleton()` | 앱 전체에서 인스턴스 하나 | **RootScope만** (다른 곳에서 사용 시 빌드 에러) |
| `AsScoped()` | 해당 Scope 내에서 인스턴스 하나 | 모든 Scope |
| `AsTransient()` | Resolve할 때마다 새 인스턴스 | 모든 Scope |
| `FromInstance()` | 이미 생성된 인스턴스 등록 | 모든 Scope (Scoped로 처리) |

**Singleton을 RootScope에서만 허용하는 이유**: child scope에서 Singleton을 등록하면, scope 파괴 시 인스턴스도 파괴되어 "Singleton"이라는 의미와 모순된다. `ScopeBuilder.Build()` 시점에 parent가 존재하면 Singleton 등록을 차단하여 이 혼란을 원천 방지한다.

**Transient + IUpdatable을 금지하는 이유**: Resolve마다 identity가 바뀌는 객체는 player loop 등록 단위로 안전하게 관리할 수 없다. 유닛처럼 다수 인스턴스에 매 프레임 로직이 필요하면 ① 유닛을 프리팹 + `DIBehaviour`로 만들거나(Unity Update 사용), ② Scoped `IUpdatable` 매니저 하나가 유닛 상태 목록을 순회하는 구조를 권장한다.

`IDisposable` 또는 `IInjectable`인 Transient는 성공한 Resolve마다 Scope의 activation order에 기록되어 Scope 종료 시 역순 정리된다. 주입 필드 복원까지 Scope가 책임지므로 호출자가 별도로 Dispose하면 안 된다. 따라서 긴 root Scope에서 이 Transient를 무제한으로 Resolve하지 말고, 대량 작업은 짧은 child Scope로 경계를 만든다.

팩토리가 반환한 `IDisposable`은 최초 activation Scope가 소유한다. 같은 identity를 같은 Scope의 여러 타입으로 노출할 때는 `AlsoBind`, 외부 공유 객체는 `FromInstance`를 사용한다. 모든 `FromInstance` identity는 activation 전에 선행 확정되므로 같은 builder 안의 등록 순서가 소유권을 바꾸지 않는다. 동일 객체를 sibling Scope의 여러 factory가 반환하면 한 Scope의 Dispose가 다른 Scope를 깨뜨리므로 즉시 실패한다. cached Unity 서비스가 외부에서 파괴되면 기존 소비자의 필드도 더는 안전하지 않으므로 MonoBehaviour/GameObject lifetime callback은 즉시, 그 밖의 Component/ScriptableObject는 전역 lifetime monitor와 managed callback 직전의 Scope-chain 검사가 owning Scope 전체를 fail-closed한다. 따라서 앞선 updater가 hostless Unity 서비스를 파괴해도 같은 phase의 뒤 consumer는 중간 C# 서비스를 거친 전이 참조까지 관찰하기 전에 중단된다. Hostless Unity 객체, 먼저 주입된 sibling 또는 Scope-owned clone이 activation 중 파괴되면 savepoint-aware transaction 경계 검사에서 성공 반환 전에 partial graph를 rollback한다. public direct injection target이 activation 밖에서 파괴되면 해당 lease만 철회한다. Resolve가 먼저 호출되어도 destroyed cache 검사가 같은 종료를 수행한다. Unity 객체를 독립적으로 파괴해야 한다면 서비스로 등록하지 말고 external-owned GameObject로 관리한다.

**Scope Freeze**: `Build()` 이후에는 `ScopeBuilder`에 추가 등록이 불가능하다. 런타임 중 등록 변경으로 인한 추적 불가 버그를 방지한다.

### 팩토리 등록

복잡한 생성 로직이나 DI가 아닌 runtime 값이 필요한 경우 zero-argument 팩토리를 사용한다. 서비스 의존성은 생성자나 resolver 인자가 아니라 다른 타입과 동일하게 `[Inject]` 필드로 선언한다:

```csharp
public sealed class BattleService : IBattleService, IInjectable
{
    [Inject] private IBattleConfig _config;
    [Inject] private ILogger _logger;

    private readonly BattleRules _rules;

    public BattleService(BattleRules rules)
    {
        _rules = rules;
    }
}

protected override void Configure(ScopeBuilder builder)
{
    // FromFactory — 외부 runtime 값만 closure로 전달
    builder.Bind<IBattleService>()
           .FromFactory(() => new BattleService(_loadedRules))
           .AsScoped();

    // RegisterFactory — ScopeBuilder 직접 API
    builder.RegisterFactory<IWeaponFactory>(
        () => new WeaponFactory(_seed), Lifetime.Scoped);

    // RegisterInstance — 인스턴스 직접 등록
    builder.RegisterInstance<IGameSettings>(loadedSettings);
}
```

factory가 raw `IScope`를 closure로 캡처하거나 factory 결과가 보관한 Scope를 `PostInject`에서 사용하더라도, 진행 중인 activation 안의 public nested `Resolve`는 거부된다. factory 결과의 `[Inject]` 필드와 그 하위 graph만 같은 transaction에 참여하며 실패 시 함께 rollback된다.

**팩토리에서의 동적 생성 — `IInstantiator` 주입**:

`IScope`는 직접 주입할 수 없다 — 임의 타입을 `Resolve`할 수 있게 되어 서비스 로케이터 안티패턴이 되기 때문이다. 대신 **Resolve 권한 없는 생성 전용 인터페이스 `IInstantiator`**가 모든 스코프에 자동 등록되어 있어 `[Inject]`로 주입받을 수 있다:

```csharp
public interface IEnemyFactory : IDependencyObject
{
    GameObject Create(EnemyType type, Vector3 position);
}

public class EnemyFactory : IEnemyFactory, IInjectable
{
    [Inject] private IInstantiator _instantiator;   // 생성 능력만 주입 — Resolve 불가

    public GameObject Create(EnemyType type, Vector3 position)
    {
        var prefab = LoadPrefab(type);
        // 프리팹 생성 + 하위 IInjectable 자동 주입
        return _instantiator.Instantiate(prefab, position, Quaternion.identity);
    }
}

// 등록 — 일반 바인딩과 동일. FromFactory로 scope를 꿰어줄 필요가 없다
builder.Bind<IEnemyFactory>().To<EnemyFactory>().AsScoped();
```

---

## 동적 객체 생성

런타임에 프리팹을 인스턴스화할 때, 주입된 코드에서는 `Object.Instantiate` 대신 `IInstantiator`를 사용해야 `[Inject]` 필드가 주입되고 현재 Scope가 clone을 소유한다:

```csharp
public class EnemySpawner : DIBehaviour
{
    [Inject] private IEnemyConfig _config;
    [SerializeField] private GameObject _enemyPrefab;

    public void SpawnEnemy(Vector3 position)
    {
        // Instantiator.Instantiate = Object.Instantiate + 하위 IInjectable 자동 주입
        var enemy = Instantiator.Instantiate(_enemyPrefab, position, Quaternion.identity);
    }

    public void InjectExisting(GameObject go)
    {
        // 이미 존재하는 GameObject에 주입
        Instantiator.InjectGameObject(go);
    }
}
```

`ScopeExtensions`가 제공하는 오버로드:

```csharp
scope.Instantiate(prefab);                               // 기본
scope.Instantiate(prefab, parent);                       // 부모 Transform 지정
scope.Instantiate(prefab, position, rotation);           // 위치/회전 지정
scope.Instantiate(prefab, position, rotation, parent);   // 전체 지정
scope.InjectGameObject(existingGameObject);              // 기존 오브젝트에 주입
```

이 raw Scope 확장 메서드는 `LifetimeScope`나 테스트 bootstrap 같은 composition boundary용이다. 일반 주입 대상은 같은 기능만 노출하는 `IInstantiator`/`DIBehaviour.Instantiator`를 사용한다.

`DIBehaviour`의 `Instantiator` 프로퍼티는 Push 주입 시 자동으로 설정된다. 동적 생성된 오브젝트도 `IInstantiator.Instantiate()`를 통하면 내부의 `DIBehaviour`가 같은 생성 권한을 전달받는다. active prefab은 목적 Scene의 비활성 staging hierarchy에서 clone root까지 먼저 비활성화한 뒤 전체 주입과 `PostInject`를 마치고 최종 hierarchy로 이동하므로 `Awake/OnEnable`은 주입 이후 실행된다. parent를 생략한 인스턴스는 active Scene에 남으며 `DontDestroyOnLoad` Scene으로 이동하지 않는다.

prefab root의 최초 `activeSelf`는 **최종 활성화 여부**로 별도 보존된다. 실제 clone root는 사용자 callback 전에 항상 `false`가 되며, `PostInject`가 끝날 때까지 그대로여야 한다. 내부 sentinel은 staging GameObject가 실제 활성 hierarchy에 들어간 이력과 clone root가 staging 밖으로 나갔다 돌아온 이력을 기록하므로, 최종 상태만 원복해도 해당 생성은 실패하고 clone/DI 기록이 rollback된다. `PostInject`가 root를 `true`로 남긴 경우도 최종 검증에서 실패한다.

Unity는 비활성 hierarchy 안에서 일어난 clone root의 `true → false` 왕복 자체를 과거 이력으로 제공하지 않는다. KDI는 이를 탐지한다고 가정하지 않고, staging과 최종 배치 전 root를 모두 비활성으로 유지해 그 왕복이 `Awake/OnEnable`을 조기 실행하지 못하도록 격리한다. callback이 직접 수행한 임의의 외부 side effect까지 되돌리는 것은 아니므로 `PostInject`는 root/staging을 조작하지 않고 주입된 필드만으로 idempotent하게 초기화해야 한다. active prefab은 inactive destination hierarchy 아래에 만들 수 없다. 반대로 처음부터 inactive인 prefab은 inactive parent 아래에 둘 수 있지만, 이후 caller가 활성화하는 시점의 일반 Unity callback은 완료된 `Instantiate` activation attempt의 보상 경계 밖이므로 caller가 그 활성화 수명을 명시적으로 소유한다.

최종 활성화가 시작되면 KDI는 별도의 activation attempt를 열어 이미 commit된 parent clone 소유 기록, 주입 lease, 활성화 중 새로 만들어진 child Scope와 서비스 기록을 receipt로 유지한다. `LifetimeScope.Initialize`나 `DIBehaviour.OnInjectedEnable` 실패는 Unity가 `Awake/OnEnable` 예외를 호출자에게 전달하지 않더라도 attempt에 명시적으로 기록된다. 활성화가 완전히 끝나기 전에 실패하면 clone을 먼저 비활성화하고 child Scope와 activation record를 역순 보상한 뒤 clone을 파괴하므로, 실패한 spawn이 성공값이나 parent 소유 기록으로 남지 않는다.

`Scope.Instantiate()`의 clone은 **Scope-owned**다. Scope 종료 시 먼저 비활성화되어 `OnDisable`이 아직 주입된 필드를 볼 수 있고, 주입 철회 뒤 GameObject가 파괴된다. clone을 먼저 파괴하면 lifetime host가 소유 기록도 즉시 제거한다. 반대로 `InjectGameObject(existingGameObject)`는 **external-owned**이므로 GameObject를 파괴하지 않는다. 외부 소유자는 Scope보다 오래 남길 객체를 Scope 종료 전에 비활성화/파괴하거나 새 Scope로 다시 주입할 전환 지점을 직접 관리해야 한다.

주입된 컴포넌트가 먼저 파괴되면 내부 lifetime host가 해당 lease를 즉시 철회한다. 따라서 장수 Scope에서 prefab을 반복 생성/파괴해도 파괴된 컴포넌트와 `InjectionDisposables`가 Scope 종료까지 누적되지 않는다. Scope가 먼저 종료되는 경우에도 같은 lease를 idempotent하게 철회한다.

prefab hierarchy의 parentless `LifetimeScope`는 `Scope.Instantiate()` 호출 Scope의 runtime child로 준비된다. 활성화 후 별도 primary root를 만들지 않으며, prefab 내부의 중첩 parentless `LifetimeScope`는 가장 가까운 상위 `LifetimeScope`의 child가 된다.

`Scope.Instantiate()`는 factory, `PostInject`, 최초 주입 중의 `OnInjectedEnable`처럼 activation transaction이 열린 callback 안에서는 사용할 수 없다. 그 시점에 clone을 외부 세계에 만들면 이후 graph rollback이 GameObject 수명까지 원복할 수 없기 때문이다. 생성은 Scope build/Resolve가 성공한 뒤의 명시적인 gameplay 단계에서 호출한다(후속 enable 구간처럼 activation 밖의 `OnInjectedEnable`은 허용). 기존 GameObject를 주입하는 `InjectGameObject()`는 activation transaction에 참여하므로 이 제한과 별개다.

MonoBehaviour가 아닌 순수 C# 클래스(팩토리, 서비스)에서도 `[Inject] IInstantiator`를 사용한다 (위 [팩토리 등록](#팩토리-등록) 참고).

### 늦게 생성되는 오브젝트 — "만드는 쪽이 주입한다"

Push 주입은 `LifetimeScope` 초기화 시점에 한 번 실행된다. 그 이후에 등장하는 오브젝트(additive 씬 로드, 런타임 조립 등)는 **받는 쪽이 스스로 주입을 당겨오지 않고, 그것을 만든/로드한 쪽이 주입한다**:

```csharp
// additive 씬 로드 — 로드한 쪽이 씬 루트에 주입
var op = SceneManager.LoadSceneAsync("BattleUI", LoadSceneMode.Additive);
op.completed += _ =>
{
    foreach (var root in SceneManager.GetSceneByName("BattleUI").GetRootGameObjects())
        Instantiator.InjectGameObject(root);   // 로드한 주입 객체가 명시적으로 전달
};
```

객체가 전역에서 스코프를 찾아 셀프 주입하는 방식은 지원하지 않는다 — 잘못된 하이어라키 배치가 조용히 가려지고, Push 모델의 "주입 시점이 결정적"이라는 보장이 깨지기 때문이다.

---

## Update Loop 시스템

MonoBehaviour가 아닌 순수 C# 클래스에서 매 프레임 로직이 필요할 때 사용한다. Scope를 통해 Resolve되면 `UpdateLoopManager`에 **자동 등록**되고, Scope Dispose 시 **자동 해제**된다. public 수동 등록은 반드시 canonical `UpdateLoopManager.Instance`에서 수행하며 scene-added/직접 생성 manager는 별도 소유권 기관이 될 수 없다. 동일 identity를 public API와 Scope에서 동시에 등록하거나 direct-injected identity를 수동 등록하면 주입 철회 뒤 수동 callback만 남을 수 있으므로 fail-fast한다. `LifetimeScope.Configure`, activation transaction, prefab final activation 중 public 수동 등록/해제도 rollback할 수 없는 외부 변경이므로 허용하지 않는다. Scope-managed 등록은 public `Unregister`가 아니라 owning Scope Dispose로 해제하고, 주입과 managed update가 모두 필요하면 수동 등록 대신 `FromInstance`로 노출한다. manager GameObject가 예기치 않게 파괴되면 등록과 lifetime monitor를 replacement manager로 이전한다.

Hostless Unity 객체는 Unity destruction callback이 없으므로 managed callback 직전에 owning Scope와 ancestor의 polling record를 검사한다. 따라서 정확성 비용은 최악의 경우 updater 수 × 해당 Scope-chain의 hostless record 수에 비례한다. 대량의 프레임 업데이트 dependency는 가능한 한 callback 기반 `MonoBehaviour`/`GameObject` 수명으로 두고, 독립 파괴가 필요한 `ScriptableObject`/비-Mono Component 서비스는 Scope당 소수로 유지한다.

```csharp
// Update 루프
public class GameSimulation : IDependencyObject, IInjectable, IUpdatable
{
    [Inject] private IGameState _state;

    public void KDIUpdate(float deltaTime)
    {
        _state.Tick(deltaTime);
    }
}

// FixedUpdate 루프
public class PhysicsSimulation : IDependencyObject, IFixedUpdatable
{
    public void KDIFixedUpdate(float fixedDeltaTime)
    {
        StepSimulation(fixedDeltaTime);
    }
}

// LateUpdate 루프
public class CameraFollow : IDependencyObject, ILateUpdatable
{
    public void KDILateUpdate(float deltaTime)
    {
        UpdateCameraPosition(deltaTime);
    }
}
```

### 실행 순서 제어

`IUpdatePriority`를 구현하면 실행 순서를 제어할 수 있다. **값이 낮을수록 먼저 실행**된다:

```csharp
public class InputProcessor : IDependencyObject, IUpdatable, IUpdatePriority
{
    public int UpdatePriority => -100;  // 가장 먼저 실행

    public void KDIUpdate(float deltaTime) { /* 입력 처리 */ }
}

public class GameLogic : IDependencyObject, IUpdatable, IUpdatePriority
{
    public int UpdatePriority => 0;     // 기본값 (입력 처리 이후)

    public void KDIUpdate(float deltaTime) { /* 게임 로직 */ }
}

public class Renderer : IDependencyObject, IUpdatable, IUpdatePriority
{
    public int UpdatePriority => 100;   // 가장 나중에 실행

    public void KDIUpdate(float deltaTime) { /* 렌더링 준비 */ }
}
```

`IUpdatePriority`를 구현하지 않으면 기본 우선순위 0으로 동작한다.

---

## SubscribableProperty (반응형 프로퍼티)

값 변경을 관찰할 수 있는 반응형 프로퍼티 시스템. UI 바인딩, 상태 동기화에 사용한다. 별도 외부 라이브러리(UniRx, R3) 없이 프레임워크에 내장되어 있다.

### 기본 사용

```csharp
// 서비스에서 상태 노출
public class PlayerService : IDependencyObject
{
    public SubscribableProperty<int> Health { get; } = new(100);
    public SubscribableProperty<string> Name { get; } = new("Player");
}

// UI에서 구독
public class PlayerHUD : DIBehaviour
{
    [Inject] private PlayerService _player;
    [SerializeField] private TMP_Text _healthText;

    protected override void OnInjectedEnable()
    {
        _player.Health
            .Subscribe(hp => _healthText.text = $"HP: {hp}", invokeInitial: true)
            .AddTo(_cd);
    }
}
```

`Subscribe`의 `invokeInitial: true`는 구독 시점에 현재 값으로 즉시 콜백을 호출한다. `.AddTo(_cd)`로 비활성화 시 구독이 해제되며, 재활성화되면 `OnInjectedEnable`에서 새 구독을 만든다.

### LINQ 변환

```csharp
// Select — 값 변환
_player.Health
    .Select(hp => hp / 100f)  // int → float (0.0~1.0)
    .Subscribe(ratio => _slider.value = ratio)
    .AddTo(_cd);

// Filter — 상태값이 없는 알림 조건 필터링
_player.Health
    .Filter(hp => hp <= 0)
    .Subscribe(_ => ShowDeathScreen())
    .AddTo(_cd);
```

### SubscribableCollection

리스트의 변경(추가/삭제/교체/이동/초기화)을 개별적으로 관찰할 수 있다:

```csharp
public class InventoryService : IDependencyObject
{
    public SubscribableCollection<Item> Items { get; } = new();
}

public class InventoryUI : DIBehaviour
{
    [Inject] private InventoryService _inventory;

    protected override void OnInjectedEnable()
    {
        // 전체 변경 구독
        _inventory.Items.Subscribe(change =>
        {
            switch (change.Type)
            {
                case CollectionChangeType.Add:
                    CreateSlot(change.Index, change.NewValue);
                    break;
                case CollectionChangeType.Remove:
                    RemoveSlot(change.Index);
                    break;
                case CollectionChangeType.Clear:
                    ClearAllSlots();
                    break;
            }
        }, invokeForExisting: true).AddTo(_cd);

        // 특정 이벤트만 구독
        _inventory.Items.SubscribeAdd((index, item) => CreateSlot(index, item)).AddTo(_cd);
        _inventory.Items.SubscribeCount(count => UpdateCountText(count), invokeInitial: true).AddTo(_cd);
    }
}
```

### SubscribableDictionary

```csharp
public SubscribableDictionary<string, int> Stats { get; } = new();

Stats.SubscribeAdd((key, value) => Debug.Log($"스탯 추가: {key}={value}")).AddTo(_cd);
Stats.SubscribeReplace((key, oldVal, newVal) => Debug.Log($"스탯 변경: {key} {oldVal}→{newVal}")).AddTo(_cd);
```

### SubscribableCommand

조건부 실행이 가능한 커맨드 패턴:

```csharp
var canAttack = new SubscribableProperty<bool>(true);
var attackCommand = new SubscribableCommand(canAttack, () => PerformAttack());

// canAttack.Value가 true일 때만 실행됨
attackCommand.Execute();

// UI 바인딩 — 버튼 활성화 상태 동기화
attackCommand.CanExecute
    .Subscribe(can => _attackButton.interactable = can)
    .AddTo(_cd);
```

---

## 디버그 도구

### LifetimeScope 인스펙터 (에디터 전용)

`LifetimeScope` 컴포넌트를 선택하면 Play 모드에서 스코프 내부가 표시된다:

```
▼ Battle Scope (Script)
    Parent          AppRootScope
    Registrations (3)
      IUnitCatalog   → UnitCatalog    Scoped   ● resolved
      ISpawnUnitApp  → SpawnUnitApp   Scoped   ● resolved
      ISpawnVM       → SpawnVM        Scoped   ○ not yet
```

등록 목록, Lifetime, Resolve 여부를 실시간으로 확인할 수 있어 "왜 주입이 안 되지?"를 코드 없이 진단할 수 있다.

또한 Resolve 실패와 순환참조 에러 메시지에 **스코프 체인**이 포함된다:

```
[KDI] BattleScope → AppRootScope 체인에서 IUnitCatalog 등록을 찾을 수 없습니다.
[KDI] (BattleScope) 순환참조 발생: IUnitDomain → IUnitRepository → IUnitDomain
```

### Closure Profiler (에디터 전용, `com.kylin.subscribable` 패키지 포함)

`SubscribableProperty` 구독 시 생성되는 클로저의 메모리 캡처를 분석하는 에디터 윈도우. 메모리 누수 진단에 유용하다.

- `this` 캡처 감지 (Critical 위험도)
- 캡처된 변수별 메모리 추정
- 활성/해제된 구독 히스토리 추적
- `ClosureProfilerWindow`에서 실시간 모니터링

---

## 상용 DI 프레임워크와의 비교

### 기능 비교

| 항목 | VContainer | Zenject | KDI |
|------|-----------|---------|-----|
| **주입 방식** | 생성자 + 메서드 + 필드 | 생성자 + 메서드 + 필드 + 프로퍼티 | **필드 전용** |
| **Scope 모델** | LifetimeScope 계층 | Context 계층 | LifetimeScope 계층 |
| **인스턴스 생성** | IL Emit / Source Generator | Reflection + 캐시 | 등록 시 캡처된 `new()` 델리게이트 (AOT 안전) |
| **순환 참조 감지** | 있음 | 있음 | 있음 (`ThreadStatic`) |
| **Update 루프** | ITickable 등 | ITickable 등 | IUpdatable / IFixedUpdatable / ILateUpdatable |
| **반응형 시스템** | 없음 (외부 R3 필요) | 없음 (외부 UniRx 필요) | **내장** (SubscribableProperty) |
| **코드 규모** | ~수천 줄 | ~수만 줄 | 소스 공개형 경량 코어 + 명시적 수명/rollback ledger |
| **학습 곡선** | 보통 | 높음 | **낮음** |

### 왜 필드 주입만 사용하는가

KDI는 **의도적으로 생성자 주입을 지원하지 않는다.** 이것은 제한이 아니라 설계 결정이다.

1. **Unity 호환성**: `MonoBehaviour`는 생성자를 사용할 수 없다. 필드 주입으로 통일하면 MonoBehaviour든 순수 C# 클래스든 **동일한 패턴**으로 DI를 사용한다. "이 클래스는 생성자 주입, 저 클래스는 필드 주입"이라는 혼란이 없다.

2. **고속·안정 인스턴스 생성**: 모든 DI 관리 타입이 파라미터 없는 생성자를 가지므로, `To<TImpl>()` 등록 시점에 `() => new TImpl()` 델리게이트를 정적으로 캡처한다. 표현식 컴파일(JIT)·리플렉션이 없어 IL2CPP(AOT)에서 안정적이고 코드 스트리핑 영향을 받지 않으며, 생성자 인자 해석 오버헤드도 없다. (`new()` 제약 덕에 파라미터 없는 생성자 누락은 컴파일 타임에 잡힌다.)

3. **학습 비용 최소화**: `[Inject]`를 필드에 붙이면 끝. 팩토리 메서드 시그니처, 생성자 파라미터 순서, `[Inject]` vs 생성자 선택 고민이 없다.

### KDI의 장점

- **소스 투명성**: 생성 코드나 런타임 코드젠 없이 Scope·activation ledger·Unity lifetime 연결이 패키지 소스에 명시되어 있어, 주입과 해제 경로를 직접 추적할 수 있다.
- **하나의 패턴**: 필드 주입만 지원하므로 프로젝트 전체가 일관된 스타일을 유지한다. 코드 리뷰에서 "왜 여기는 생성자 주입이지?"라는 논쟁이 없다.
- **반응형 시스템 내장**: `SubscribableProperty`, `SubscribableCollection`, `SubscribableDictionary`가 프레임워크에 포함되어 별도 라이브러리 의존 없이 옵저버 패턴을 사용할 수 있다.
- **Unity 친화적 설계**: 하이어라키 기반 Push 주입, Transform.IsChildOf 기반 Scope 탐색, MonoBehaviour 생명주기와의 자연스러운 통합.
- **안전한 구독 관리**: `DIBehaviour`의 `_cd` + `AddTo()` 패턴으로 `OnDisable` 시 구독이 자동 정리된다. 메모리 누수 걱정 없이 사용 가능하다.

### KDI의 한계

- **필드 주입 전용**: 생성자 주입이 필요한 아키텍처(CQRS 핸들러 자동 등록 등)에는 적합하지 않다.
- **순수 C# 프로젝트 미지원**: Unity 6 전용이며, `MonoBehaviour`/`Transform` 기반 설계다.
- **대규모 팀 관습 차이**: VContainer/Zenject에 익숙한 팀원이 있다면 필드 주입 전용 방식에 적응이 필요하다.
- **생태계 규모**: 상용 프레임워크 대비 커뮤니티 지원, 서드파티 통합이 적다.
- **Enter Play Mode 설정**: Domain Reload와 Scene Reload를 동시에 끈 상태의 자동 복원은 지원하지 않는다. 이전 세션 Scope가 남으면 stale 의존성 실행을 막기 위해 SubsystemRegistration에서 dispose/deactivate한다. 이 조합을 사용해야 한다면 editor bootstrap이 Scope를 명시적으로 다시 활성화·초기화해야 한다.
- **Scope 구현 경계**: `IScope`는 소비자 추상화지만, custom 구현을 KDI child parent로 사용하거나 그 구현으로 `Inject`/`InjectGameObject`/`Instantiate`할 수 없다. 하나의 concrete KDI activation/lifetime ledger가 원자적 rollback과 역순 철회를 끝까지 담당해야 한다.

---

## 전체 예시

```csharp
// ── 인터페이스 ──
public interface IPlayerService : IDependencyObject
{
    SubscribableProperty<int> Health { get; }
    void TakeDamage(int amount);
}

public interface IAudioService : IDependencyObject
{
    void PlaySFX(string clipName);
}

// ── 구현 ──
public class PlayerService : IPlayerService, IInjectable
{
    [Inject] private IAudioService _audio;

    public SubscribableProperty<int> Health { get; } = new(100);

    public void TakeDamage(int amount)
    {
        Health.Value = Mathf.Max(0, Health.Value - amount);
        _audio.PlaySFX("hit");
    }
}

public class AudioService : IAudioService
{
    public void PlaySFX(string clipName) { /* 재생 로직 */ }
}

// ── Scope 등록 ──
public class GameRootScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IAudioService>().To<AudioService>().AsSingleton();
    }
}

public class BattleScope : LifetimeScope
{
    // Inspector에서 _parent = GameRootScope 지정
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IPlayerService>().To<PlayerService>().AsScoped();
    }
}

// ── UI ──
public class HealthBar : DIBehaviour
{
    [Inject] private IPlayerService _player;
    [SerializeField] private Slider _slider;

    protected override void OnInjectedEnable()
    {
        _player.Health
            .Select(hp => hp / 100f)
            .Subscribe(ratio => _slider.value = ratio, invokeInitial: true)
            .AddTo(_cd);
    }
}
```

씬 하이어라키:
```
[GameRootScope]                 ← RootScope (Singleton 등록)
  └── [BattleScope]             ← ChildScope (Inspector에서 parent 지정)
        ├── Player
        │     └── HealthBar     ← DIBehaviour, [Inject] 자동 주입
        └── EnemySpawner        ← DIBehaviour, Instantiator.Instantiate()로 동적 생성
```
