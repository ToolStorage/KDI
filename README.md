# KDI (Kylin Dependency Injection)

Unity 6 전용 Scope 기반 경량 DI 프레임워크. 필드 주입 전용, 계층적 Scope, 반응형 프로퍼티 내장.

```
com.kylin.di | Unity 6000.0+ | MIT License
```

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

Unity Package Manager에서 Git URL로 추가:

```
https://github.com/ToolStorage/KDI.git
```

또는 `Packages/manifest.json`에 직접 추가:

```json
{
  "dependencies": {
    "com.kylin.di": "https://github.com/ToolStorage/KDI.git"
  }
}
```

의존성 `com.kylin.subscribable`(SubscribableProperty 반응형 시스템)이 함께 설치된다.

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

    void Start()
    {
        _scoreService.Score
            .Subscribe(score => _scoreText.text = $"Score: {score}", invokeInitial: true)
            .AddTo(_cd);  // OnDisable 시 자동 구독 해제
    }
}
```

`DIBehaviour`가 제공하는 것:
- `[Inject]` 필드 자동 주입 (`IInjectable` 구현 내장)
- `_cd` (`CompositeDisposable`) — `OnDisable` 시 모든 구독 자동 정리
- `Scope` 프로퍼티 — 현재 주입된 Scope 접근 (동적 생성 시 사용)

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

`_parent`가 `null`인 LifetimeScope는 **RootScope**로 동작하며, 프레임워크 내부의 RootScope 참조로 자동 등록된다 (외부에서 직접 접근하는 API는 제공하지 않는다 — 전역 Resolve 진입점은 서비스 로케이터가 되기 때문).

`_autoInitialize`(기본값 `true`)를 `false`로 설정하면 `Awake`에서 자동 초기화하지 않고, 수동으로 `Initialize()`를 호출해야 한다. parent가 아직 초기화되지 않은 경우 자동으로 parent를 먼저 초기화한다.

### Resolution 우선순위

Resolve 요청은 **현재 Scope → 부모 Scope → ... → RootScope** 순으로 탐색한다:

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

    // 팩토리 등록 — 복잡한 생성 로직이 필요할 때
    builder.Bind<IService>().FromFactory(scope => {
        var dep = scope.Resolve<IDependency>();
        return new ServiceImpl(dep);
    }).AsScoped();
}
```

`To<T>()`의 타입 제약: `T`는 반드시 `IDependencyObject`와 바인딩 인터페이스를 동시에 구현해야 한다.

**Build 타임 검증** — 아래 실수는 조용히 넘어가지 않고 `Build()`(= LifetimeScope 초기화) 시점에 즉시 에러가 된다:

| 실수 | 결과 |
|------|------|
| `.AsScoped()` 등 종결 메서드 누락 (`Bind().To()`까지만 작성) | Build 에러 |
| 같은 스코프에 같은 서비스 타입 중복 등록 | Build 에러 (오버라이드는 자식 스코프에서) |
| child scope에 `AsSingleton()` 등록 | Build 에러 |
| Transient + `IUpdatable` 계열 조합 | Build 에러 (아래 Lifetime 규칙 참고) |
| Transient + `IDisposable` 조합 | 경고 (Dispose는 생성한 쪽 책임) |

### Lifetime 규칙

| Lifetime | 동작 | 등록 위치 |
|----------|------|-----------|
| `AsSingleton()` | 앱 전체에서 인스턴스 하나 | **RootScope만** (다른 곳에서 사용 시 빌드 에러) |
| `AsScoped()` | 해당 Scope 내에서 인스턴스 하나 | 모든 Scope |
| `AsTransient()` | Resolve할 때마다 새 인스턴스 | 모든 Scope |
| `FromInstance()` | 이미 생성된 인스턴스 등록 | 모든 Scope (Scoped로 처리) |

**Singleton을 RootScope에서만 허용하는 이유**: child scope에서 Singleton을 등록하면, scope 파괴 시 인스턴스도 파괴되어 "Singleton"이라는 의미와 모순된다. `ScopeBuilder.Build()` 시점에 parent가 존재하면 Singleton 등록을 차단하여 이 혼란을 원천 방지한다.

**Transient + IUpdatable을 금지하는 이유**: Transient 인스턴스는 스코프가 수명을 추적하지 않으므로, Update 루프에 등록되면 스코프 파괴 후에도 영원히 호출되는 누수가 된다. 유닛처럼 다수 인스턴스에 매 프레임 로직이 필요하면 ① 유닛을 프리팹 + `DIBehaviour`로 만들거나(Unity Update 사용), ② Scoped `IUpdatable` 매니저 하나가 유닛 상태 목록을 순회하는 구조를 권장한다.

**Scope Freeze**: `Build()` 이후에는 `ScopeBuilder`에 추가 등록이 불가능하다. 런타임 중 등록 변경으로 인한 추적 불가 버그를 방지한다.

### 팩토리 등록

복잡한 생성 로직이나 외부 인자가 필요한 경우 팩토리를 사용한다:

```csharp
protected override void Configure(ScopeBuilder builder)
{
    // FromFactory — Scope 접근 가능
    builder.Bind<IBattleService>().FromFactory(scope => {
        var config = scope.Resolve<IBattleConfig>();
        var logger = scope.Resolve<ILogger>();
        var service = new BattleService();
        service.Initialize(config, logger);
        return service;
    }).AsScoped();

    // RegisterFactory — ScopeBuilder 직접 API
    builder.RegisterFactory<IWeaponFactory>(scope => {
        return new WeaponFactory(scope);
    }, Lifetime.Scoped);

    // RegisterInstance — 인스턴스 직접 등록
    builder.RegisterInstance<IGameSettings>(loadedSettings);
}
```

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

런타임에 프리팹을 인스턴스화할 때, `Object.Instantiate` 대신 `IScope` 확장 메서드를 사용해야 `[Inject]` 필드가 주입된다:

```csharp
public class EnemySpawner : DIBehaviour
{
    [Inject] private IEnemyConfig _config;
    [SerializeField] private GameObject _enemyPrefab;

    public void SpawnEnemy(Vector3 position)
    {
        // Scope.Instantiate = Object.Instantiate + 하위 IInjectable 자동 주입
        var enemy = Scope.Instantiate(_enemyPrefab, position, Quaternion.identity);
    }

    public void InjectExisting(GameObject go)
    {
        // 이미 존재하는 GameObject에 주입
        Scope.InjectGameObject(go);
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

`DIBehaviour`의 `Scope` 프로퍼티는 Push 주입 시 자동으로 설정된다. 동적 생성된 오브젝트도 `Scope.Instantiate()`를 통하면 내부의 `DIBehaviour`에 Scope가 올바르게 설정된다.

MonoBehaviour가 아닌 순수 C# 클래스(팩토리, 서비스)에서는 `Scope` 프로퍼티 대신 `[Inject] IInstantiator`를 사용한다 (위 [팩토리 등록](#팩토리-등록) 참고).

### 늦게 생성되는 오브젝트 — "만드는 쪽이 주입한다"

Push 주입은 `LifetimeScope` 초기화 시점에 한 번 실행된다. 그 이후에 등장하는 오브젝트(additive 씬 로드, 런타임 조립 등)는 **받는 쪽이 스스로 주입을 당겨오지 않고, 그것을 만든/로드한 쪽이 주입한다**:

```csharp
// additive 씬 로드 — 로드한 쪽이 씬 루트에 주입
var op = SceneManager.LoadSceneAsync("BattleUI", LoadSceneMode.Additive);
op.completed += _ =>
{
    foreach (var root in SceneManager.GetSceneByName("BattleUI").GetRootGameObjects())
        Scope.InjectGameObject(root);   // DIBehaviour/LifetimeScope가 쥔 Scope에서 호출
};
```

객체가 전역에서 스코프를 찾아 셀프 주입하는 방식은 지원하지 않는다 — 잘못된 하이어라키 배치가 조용히 가려지고, Push 모델의 "주입 시점이 결정적"이라는 보장이 깨지기 때문이다.

---

## Update Loop 시스템

MonoBehaviour가 아닌 순수 C# 클래스에서 매 프레임 로직이 필요할 때 사용한다. Scope를 통해 Resolve되면 `UpdateLoopManager`에 **자동 등록**되고, Scope Dispose 시 **자동 해제**된다.

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

    void Start()
    {
        _player.Health
            .Subscribe(hp => _healthText.text = $"HP: {hp}", invokeInitial: true)
            .AddTo(_cd);
    }
}
```

`Subscribe`의 `invokeInitial: true`는 구독 시점에 현재 값으로 즉시 콜백을 호출한다. `.AddTo(_cd)`로 `OnDisable` 시 구독이 자동 해제된다.

### LINQ 변환

```csharp
// Select — 값 변환
_player.Health
    .Select(hp => hp / 100f)  // int → float (0.0~1.0)
    .Subscribe(ratio => _slider.value = ratio)
    .AddTo(_cd);

// Where — 조건 필터링
_player.Health
    .Where(hp => hp <= 0)
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

    void Start()
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
| **인스턴스 생성** | IL Emit / Source Generator | Reflection + 캐시 | `Expression.Compile` 캐시 |
| **순환 참조 감지** | 있음 | 있음 | 있음 (`ThreadStatic`) |
| **Update 루프** | ITickable 등 | ITickable 등 | IKDIUpdatable 등 |
| **반응형 시스템** | 없음 (외부 R3 필요) | 없음 (외부 UniRx 필요) | **내장** (SubscribableProperty) |
| **코드 규모** | ~수천 줄 | ~수만 줄 | **~500줄** (코어) |
| **학습 곡선** | 보통 | 높음 | **낮음** |

### 왜 필드 주입만 사용하는가

KDI는 **의도적으로 생성자 주입을 지원하지 않는다.** 이것은 제한이 아니라 설계 결정이다.

1. **Unity 호환성**: `MonoBehaviour`는 생성자를 사용할 수 없다. 필드 주입으로 통일하면 MonoBehaviour든 순수 C# 클래스든 **동일한 패턴**으로 DI를 사용한다. "이 클래스는 생성자 주입, 저 클래스는 필드 주입"이라는 혼란이 없다.

2. **고속 인스턴스 생성**: 모든 DI 관리 타입이 파라미터 없는 생성자를 가지므로, `Expression.Lambda.Compile()` 기반 고속 팩토리 캐시가 가능하다. 생성자 인자 해석 오버헤드가 없다.

3. **학습 비용 최소화**: `[Inject]`를 필드에 붙이면 끝. 팩토리 메서드 시그니처, 생성자 파라미터 순서, `[Inject]` vs 생성자 선택 고민이 없다.

### KDI의 장점

- **극단적 단순성**: 코어 DI 로직 500줄 미만. 전체 소스를 읽고 이해하는 데 30분이면 충분하다. 프레임워크 내부 동작이 투명하다.
- **하나의 패턴**: 필드 주입만 지원하므로 프로젝트 전체가 일관된 스타일을 유지한다. 코드 리뷰에서 "왜 여기는 생성자 주입이지?"라는 논쟁이 없다.
- **반응형 시스템 내장**: `SubscribableProperty`, `SubscribableCollection`, `SubscribableDictionary`가 프레임워크에 포함되어 별도 라이브러리 의존 없이 옵저버 패턴을 사용할 수 있다.
- **Unity 친화적 설계**: 하이어라키 기반 Push 주입, Transform.IsChildOf 기반 Scope 탐색, MonoBehaviour 생명주기와의 자연스러운 통합.
- **안전한 구독 관리**: `DIBehaviour`의 `_cd` + `AddTo()` 패턴으로 `OnDisable` 시 구독이 자동 정리된다. 메모리 누수 걱정 없이 사용 가능하다.

### KDI의 한계

- **필드 주입 전용**: 생성자 주입이 필요한 아키텍처(CQRS 핸들러 자동 등록 등)에는 적합하지 않다.
- **순수 C# 프로젝트 미지원**: Unity 6 전용이며, `MonoBehaviour`/`Transform` 기반 설계다.
- **대규모 팀 관습 차이**: VContainer/Zenject에 익숙한 팀원이 있다면 필드 주입 전용 방식에 적응이 필요하다.
- **생태계 규모**: 상용 프레임워크 대비 커뮤니티 지원, 서드파티 통합이 적다.

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

    void Start()
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
        └── EnemySpawner        ← DIBehaviour, Scope.Instantiate()로 동적 생성
```
