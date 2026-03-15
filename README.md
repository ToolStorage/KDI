# KDI (Kylin Dependency Injection)

Unity 6 전용 Scope 기반 의존성 주입 프레임워크.

---

## 왜 의존성 주입(DI)이 필요한가

### 문제: 직접 의존

게임 코드에서 가장 흔한 안티패턴은 클래스가 자신이 필요로 하는 객체를 **직접 찾거나 생성**하는 것이다.

```csharp
// 나쁜 예: 직접 의존
public class EnemyAI : MonoBehaviour
{
    void Update()
    {
        // 매 프레임 씬에서 검색 — 느리고, 이름이 바뀌면 깨진다
        var player = GameObject.Find("Player").GetComponent<PlayerStatus>();

        // 싱글톤에 직접 접근 — GameManager가 없으면 NullRef
        var difficulty = GameManager.Instance.Difficulty;

        // 다른 시스템에 직접 접근 — 테스트에서 분리 불가능
        AudioManager.Instance.PlaySound("alert");
    }
}
```

이 코드의 문제점:
- **강한 결합**: `EnemyAI`가 `PlayerStatus`, `GameManager`, `AudioManager`의 구체적인 존재 방식을 모두 알아야 한다.
- **변경 전파**: `GameManager`의 구조가 바뀌면 이를 사용하는 모든 클래스를 수정해야 한다.
- **테스트 불가**: 이 클래스를 단독으로 테스트하려면 씬에 Player, GameManager, AudioManager가 모두 있어야 한다.
- **순서 의존**: `GameManager.Instance`가 아직 초기화되지 않았다면? 실행 순서에 의존하는 버그가 생긴다.

### 해결: 의존성 주입

의존성 주입의 핵심 원칙은 단순하다: **클래스는 자신이 필요한 것을 직접 찾지 않는다. 외부에서 넣어준다.**

```csharp
// 좋은 예: 의존성 주입
public class EnemyAI : DIBehaviour
{
    [Inject] private IPlayerStatus _player;
    [Inject] private IDifficultyProvider _difficulty;
    [Inject] private IAudioService _audio;

    void Update()
    {
        // 이미 주입되어 있으므로 바로 사용
        if (_player.Health.Value < 30)
            _audio.PlaySound("alert");
    }
}
```

달라진 점:
- **느슨한 결합**: `EnemyAI`는 인터페이스(`IPlayerStatus`)만 안다. 구현이 바뀌어도 이 클래스는 수정 불필요.
- **변경 격리**: 구현체 교체는 등록 한 곳에서만 하면 된다.
- **테스트 용이**: 테스트 시 Mock 구현체를 주입하면 된다.
- **순서 안전**: 프레임워크가 올바른 시점에 주입을 보장한다.

### 의존성 주입이 해결하는 Unity의 고질적 문제들

| 기존 패턴 | 문제 | DI 대안 |
|-----------|------|---------|
| `GameObject.Find()` | 문자열 의존, 느림, 이름 변경 시 런타임 에러 | `[Inject]` 필드에 자동 주입 |
| `FindObjectOfType<T>()` | 느림, 여러 인스턴스 시 비결정적 | Scope에 명시적 등록 후 주입 |
| `Singleton.Instance` | 전역 상태, 초기화 순서 문제, 테스트 불가 | Scope Lifetime 관리 (Singleton/Scoped) |
| `[SerializeField]` 수동 연결 | 씬마다 수동 드래그, 프리팹 깨짐 | 코드 레벨에서 자동 바인딩 |
| `GetComponent` 체이닝 | 컴포넌트 간 암묵적 의존, 누락 시 런타임 에러 | 명시적 인터페이스 의존 |

---

## KDI 설계 철학

### 필드 주입 전용 (Field Injection Only)

KDI는 **의도적으로 생성자 주입을 지원하지 않는다**. 이것은 제한이 아니라 설계 결정이다.

**이유:**
1. **Unity 호환성**: `MonoBehaviour`는 생성자를 사용할 수 없다. 필드 주입으로 통일하면 MonoBehaviour든 순수 C# 클래스든 동일한 패턴으로 DI를 사용한다.
2. **코드 단순성**: 생성자 주입을 허용하면 "이 클래스는 생성자 주입, 저 클래스는 필드 주입"이라는 혼란이 생긴다. 하나의 패턴으로 통일함으로써 프로젝트 전체가 일관된 스타일을 유지한다.
3. **학습 비용 최소화**: `[Inject]`를 필드에 붙이면 끝. 팩토리 메서드 시그니처나 생성자 파라미터 순서를 고민할 필요가 없다.
4. **파라미터 없는 생성자 강제**: 모든 DI 관리 타입이 파라미터 없는 생성자를 가지므로, `Expression.Compile` 기반 고속 인스턴스 생성이 가능하다.

```csharp
// KDI의 유일한 주입 방식 — 심플하고 일관적
public class BattleService : IDependencyObject, IInjectable
{
    [Inject] private IUnitRepository _unitRepo;
    [Inject] private IDamageCalculator _damageCalc;

    // 파라미터 없는 생성자 (KDI가 자동 호출)
}
```

### Scope 기반 (전역 Container 없음)

전역 Container 대신 **계층적 Scope**로 의존성의 수명을 관리한다.

```
RootScope (앱 전체 — Singleton 등록 가능)
  ├── LobbyScope (로비 씬)
  │     └── MatchmakingScope (매칭 UI)
  └── BattleScope (전투 씬)
        ├── PhysicsScope (물리 시스템)
        └── UIScope (전투 UI)
```

각 Scope가 파괴되면 해당 Scope에 등록된 인스턴스가 자동 정리된다. 씬 전환 시 메모리 누수 걱정이 없다.

---

## 빠른 시작

### 1. 서비스 정의

```csharp
// 인터페이스 정의
public interface IScoreService : IDependencyObject
{
    SubscribableProperty<int> Score { get; }
    void AddScore(int amount);
}

// 구현
public class ScoreService : IScoreService, IInjectable
{
    [Inject] private IGameConfig _config;  // 다른 서비스에 의존 가능

    public SubscribableProperty<int> Score { get; } = new(0);

    public void AddScore(int amount)
    {
        Score.Value += amount * _config.ScoreMultiplier;
    }
}
```

**핵심 인터페이스:**
- `IDependencyObject` — DI 컨테이너에 등록 가능한 타입 마커. `To<T>()`나 `FromInstance()`에 필요.
- `IInjectable` — `[Inject]` 필드 주입을 받는 타입 마커. 이걸 구현해야 필드 주입이 동작한다.
- `IPostInjectable` — (선택) 주입 완료 후 `PostInject()` 콜백이 필요할 때.

### 2. LifetimeScope에 등록

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

이 MonoBehaviour를 씬의 GameObject에 추가하면 된다. Awake 시 자동으로 Scope가 빌드되고 하위 오브젝트에 주입이 실행된다.

### 3. MonoBehaviour에서 사용

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

`DIBehaviour`를 상속하면:
- `[Inject]` 필드가 자동 주입됨
- `_cd` (CompositeDisposable)로 구독 수명 관리
- `OnDisable` 시 구독 자동 정리

---

## 등록 API

### Fluent Binding

```csharp
protected override void Configure(ScopeBuilder builder)
{
    // 인터페이스 → 구현체 바인딩
    builder.Bind<IService>().To<ServiceImpl>().AsScoped();
    builder.Bind<IService>().To<ServiceImpl>().AsSingleton();   // RootScope에서만
    builder.Bind<IService>().To<ServiceImpl>().AsTransient();

    // 기존 인스턴스 등록 (항상 Scoped)
    builder.Bind<IService>().FromInstance(existingInstance);

    // 팩토리 등록 — 복잡한 생성 로직이 필요할 때
    builder.Bind<IService>().FromFactory(scope => {
        var dep = scope.Resolve<IDependency>();
        return new ServiceImpl(dep);
    }).AsScoped();
}
```

### Lifetime 규칙

| Lifetime | 동작 | 등록 위치 |
|----------|------|-----------|
| `AsSingleton()` | 앱 전체에서 인스턴스 하나 | **RootScope만** (다른 곳에서 쓰면 빌드 에러) |
| `AsScoped()` | 해당 Scope 내에서 인스턴스 하나 | 모든 Scope |
| `AsTransient()` | Resolve할 때마다 새 인스턴스 | 모든 Scope |
| `FromInstance()` | 이미 생성된 인스턴스 등록 | 모든 Scope (Scoped로 처리) |

**Singleton을 RootScope에서만 허용하는 이유**: child scope에서 Singleton을 등록하면 scope 파괴 시 인스턴스도 파괴되는데, Singleton이라는 이름은 "영원히 살아있음"을 암시한다. 혼란 방지를 위해 컴파일 타임(Build 시점)에 차단한다.

---

## Scope 계층과 Resolution Chain

### 부모-자식 관계

```csharp
// RootScope — parent 없음
public class RootLifetimeScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IGlobalConfig>().To<GlobalConfig>().AsSingleton();
        builder.Bind<ILogger>().To<GameLogger>().AsSingleton();
    }
}

// Child Scope — Inspector에서 _parent를 RootLifetimeScope로 지정
public class BattleSceneScope : LifetimeScope
{
    protected override void Configure(ScopeBuilder builder)
    {
        builder.Bind<IBattleService>().To<BattleService>().AsScoped();
        builder.Bind<IUnitManager>().To<UnitManager>().AsScoped();
    }
}
```

### Resolution 순서

`BattleSceneScope`에서 `Resolve<ILogger>()`를 호출하면:

```
1. BattleScope._instances에 ILogger가 있는가?  → 없음
2. BattleScope._registrations에 ILogger가 있는가?  → 없음
3. Parent(RootScope)에게 위임
4. RootScope._registrations에 ILogger가 있는가?  → 있음! → 인스턴스 생성/반환
```

이 체인 덕분에 child scope는 자신에게 없는 의존성을 부모에게 자연스럽게 위임한다. 전역 서비스는 Root에 한 번만 등록하면 모든 하위 scope에서 접근 가능하다.

### Push 주입 흐름

`LifetimeScope.Initialize()` 시 일어나는 일:

```
1. Configure() → ScopeBuilder에 서비스 등록
2. Build() → Scope 생성 (parent 연결)
3. InjectChildren() → 하위 Transform 재귀 탐색
   └── child에 LifetimeScope가 있으면 → 건너뜀 (그 scope의 영역)
   └── IInjectable 컴포넌트 발견 → [Inject] 필드 주입
   └── IPostInjectable이면 → PostInject() 호출
```

이것이 **Push 주입**이다. 컴포넌트가 자신의 의존성을 "찾아오는" 것이 아니라, LifetimeScope가 하위 오브젝트를 순회하며 "밀어넣는다."

---

## 동적 객체 생성

런타임에 프리팹을 인스턴스화할 때, `Object.Instantiate` 대신 scope 확장 메서드를 사용한다.

```csharp
public class EnemySpawner : DIBehaviour
{
    [Inject] private IEnemyFactory _factory;
    [SerializeField] private GameObject _enemyPrefab;

    public void SpawnEnemy(Vector3 position)
    {
        // DI가 필요한 프리팹 — scope.Instantiate 사용
        var enemy = Scope.Instantiate(_enemyPrefab, position, Quaternion.identity);

        // 이미 생성된 GameObject에 주입이 필요할 때
        // Scope.InjectGameObject(existingGameObject);
    }
}
```

`Scope.Instantiate()`는 내부적으로 `Object.Instantiate()` + `InjectGameObject()`를 수행한다. 프리팹 안의 모든 `DIBehaviour`에 자동으로 `[Inject]` 필드가 주입된다.

---

## PostInject 패턴

주입이 완료된 후 초기화 로직이 필요할 때 `IPostInjectable`을 사용한다.

```csharp
public class BattleService : IDependencyObject, IInjectable, IPostInjectable
{
    [Inject] private IUnitRepository _unitRepo;
    [Inject] private IMapService _mapService;

    private BattleState _state;

    public void PostInject()
    {
        // 이 시점에서 _unitRepo와 _mapService가 모두 주입 완료됨
        _state = new BattleState(_unitRepo.GetAllUnits(), _mapService.CurrentMap);
    }
}
```

호출 순서: 인스턴스 생성 → `[Inject]` 필드 주입 → `PostInject()` 호출

---

## Update Loop 시스템

MonoBehaviour가 아닌 순수 C# 클래스에서 Update 루프가 필요할 때 사용한다.

```csharp
public class PhysicsSimulation : IDependencyObject, IKDIFixedUpdatable
{
    public void KDIFixedUpdate(float fixedDeltaTime)
    {
        // FixedUpdate와 동일한 타이밍에 호출
        StepSimulation(fixedDeltaTime);
    }
}

// 실행 순서 제어가 필요하면 IUpdatePriority 구현
public class InputProcessor : IDependencyObject, IKDIUpdatable, IUpdatePriority
{
    public int UpdatePriority => -100;  // 낮을수록 먼저 실행

    public void KDIUpdate(float deltaTime) { /* ... */ }
}
```

| 인터페이스 | 호출 시점 |
|-----------|----------|
| `IKDIUpdatable` | `Update()` |
| `IKDIFixedUpdatable` | `FixedUpdate()` |
| `IKDILateUpdatable` | `LateUpdate()` |

DI Scope를 통해 Resolve되면 자동으로 `UpdateLoopManager`에 등록된다. Scope가 Dispose되면 자동으로 해제된다.

---

## SubscribableProperty (반응형 프로퍼티)

값 변경을 관찰할 수 있는 프로퍼티 시스템. UI 바인딩에 적합하다.

```csharp
// 서비스에서 상태 노출
public class PlayerService : IDependencyObject
{
    public SubscribableProperty<int> Health { get; } = new(100);
    public SubscribableProperty<string> Name { get; } = new("Player");
    public SubscribableCollection<Item> Inventory { get; } = new();
}

// UI에서 구독
public class PlayerHUD : DIBehaviour
{
    [Inject] private PlayerService _player;
    [SerializeField] private TMP_Text _healthText;
    [SerializeField] private TMP_Text _nameText;

    void Start()
    {
        _player.Health
            .Subscribe(hp => _healthText.text = $"HP: {hp}", invokeInitial: true)
            .AddTo(_cd);

        _player.Name
            .Subscribe(name => _nameText.text = name, invokeInitial: true)
            .AddTo(_cd);

        // LINQ 스타일 변환
        _player.Health
            .Select(hp => hp <= 0)
            .Subscribe(isDead => HandleDeath(isDead))
            .AddTo(_cd);
    }
}
```

`AddTo(_cd)` 패턴으로 `OnDisable` 시 모든 구독이 자동 해제된다. 메모리 누수를 걱정할 필요 없다.

---

## 전체 사용 예시

```csharp
// === 인터페이스 ===
public interface IPlayerService : IDependencyObject
{
    SubscribableProperty<int> Health { get; }
    SubscribableProperty<int> Level { get; }
    void TakeDamage(int amount);
}

public interface IAudioService : IDependencyObject
{
    void PlaySFX(string clipName);
}

// === 구현 ===
public class PlayerService : IPlayerService, IInjectable
{
    [Inject] private IAudioService _audio;

    public SubscribableProperty<int> Health { get; } = new(100);
    public SubscribableProperty<int> Level { get; } = new(1);

    public void TakeDamage(int amount)
    {
        Health.Value = Mathf.Max(0, Health.Value - amount);
        _audio.PlaySFX("hit");
    }
}

public class AudioService : IAudioService
{
    public void PlaySFX(string clipName) { /* AudioSource 재생 */ }
}

// === Scope 등록 ===
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

// === UI (MonoBehaviour) ===
public class HealthBar : DIBehaviour
{
    [Inject] private IPlayerService _player;
    [SerializeField] private Slider _slider;

    void Start()
    {
        _player.Health
            .Subscribe(hp => _slider.value = hp / 100f, invokeInitial: true)
            .AddTo(_cd);
    }
}
```

**씬 구조:**
```
[GameRootScope]           ← RootScope (Singleton 등록)
  └── [BattleScope]       ← ChildScope (Scoped 등록, Inspector에서 parent 지정)
        ├── Player
        │     └── HealthBar (DIBehaviour)  ← [Inject] 자동 주입
        └── EnemySpawner (DIBehaviour)
```

---

## 상용 DI 프레임워크와의 비교

| 항목 | VContainer | Zenject | KDI |
|------|-----------|---------|-----|
| 주입 방식 | 생성자 + 메서드 + 필드 | 생성자 + 메서드 + 필드 + 프로퍼티 | **필드 전용** |
| Scope 모델 | LifetimeScope 계층 | Context 계층 | LifetimeScope 계층 |
| 인스턴스 생성 | IL Emit / Source Generator | Reflection + 캐시 | Expression.Compile 캐시 |
| 순환 참조 감지 | 있음 | 있음 | 있음 (ThreadStatic) |
| Update 루프 | ITickable 등 | ITickable 등 | IKDIUpdatable 등 |
| 반응형 시스템 | 없음 (외부 R3) | 없음 (외부 UniRx) | **내장** (SubscribableProperty) |
| 코드 규모 | ~수천 줄 | ~수만 줄 | **~500줄** (코어) |
| 학습 곡선 | 보통 | 높음 | **낮음** |

### KDI의 차별점

1. **극단적 단순성**: 코어 DI 로직이 500줄 미만. 읽고 이해하는 데 30분이면 충분하다.
2. **하나의 주입 패턴**: 필드 주입만 지원함으로써 "어떤 주입 방식을 쓸까?"라는 고민을 제거.
3. **반응형 시스템 내장**: SubscribableProperty가 프레임워크에 포함되어, 별도 라이브러리 없이 옵저버 패턴 사용 가능.
4. **Unity 친화적 설계**: MonoBehaviour의 생성자 제약을 받아들이고, 그에 맞춘 일관된 패턴 제공.

---

## 아키텍처 분석

### 잘 설계된 부분

**Scope Freeze 패턴**: `ScopeBuilder.Build()` 후 추가 등록이 불가능하다. 런타임에 등록이 변경되는 것을 원천 차단하여, "이 Scope에 뭐가 등록되어 있지?"라는 추적 문제를 제거한다.

**Push 주입과 Scope 경계**: LifetimeScope가 하위 Transform을 재귀 탐색하되, 다른 LifetimeScope를 만나면 멈춘다. 이는 Scope 간 영역 침범을 구조적으로 방지하는 깔끔한 설계다.

**Singleton 등록 제한**: child scope에서 Singleton 등록 시 빌드 타임 에러를 발생시켜, Lifetime 혼동으로 인한 버그를 사전에 차단한다.

**Expression.Compile 캐시**: 리플렉션 기반 인스턴스 생성의 성능 오버헤드를 `Expression.Lambda.Compile()`로 최소화했다. 첫 호출만 느리고 이후는 직접 `new`에 준하는 성능이다.

**ConcurrentDictionary 필드 캐시**: `DependencyInjector`가 `[Inject]` 필드 목록을 타입별로 캐싱한다. 동일 타입의 반복 주입이 리플렉션 없이 수행된다.

**계층적 Dispose**: 부모 Scope Dispose 시 자식 Scope → 인스턴스 순서로 정리된다. IDisposable, UpdateLoopManager 해제까지 누락 없이 처리한다.

**Static Registry + Transform.IsChildOf**: `GetComponentInParent`(관리 코드) 대신 `Transform.IsChildOf`(네이티브 호출)로 가장 가까운 Scope를 탐색한다. Unity API 특성을 잘 활용한 최적화.

### 개선 여지

**Resolution 실패 시 진단 메시지**: 현재 `"등록을 찾을 수 없습니다"`만 출력된다. 어떤 Scope 체인을 탐색했는지, 어떤 타입이 어디에 등록되어 있는지 힌트를 주면 디버깅이 쉬워진다.

**IInjectable 누락 경고**: `[Inject]` 필드가 있는데 `IInjectable`을 구현하지 않으면 조용히 무시된다. 에디터 타임에 경고를 주는 Analyzer가 있으면 실수를 줄일 수 있다.

**Scope당 등록 목록 조회 API**: 디버깅 시 특정 Scope에 어떤 타입이 등록되어 있는지 확인할 공개 API가 없다. `Scope.GetRegisteredTypes()` 같은 진단용 메서드가 있으면 편리하다.

---

## 요약

KDI는 "DI 프레임워크가 복잡할 필요는 없다"는 철학을 실현한 프레임워크다.

- **필드 주입 전용** → 패턴 하나만 익히면 된다
- **Scope 기반** → 수명 관리가 자동이다
- **Push 주입** → 컴포넌트가 의존성을 찾아다닐 필요 없다
- **SubscribableProperty 내장** → 별도 라이브러리 없이 반응형 프로그래밍

500줄 미만의 코어 코드로 상용 DI 프레임워크의 핵심 기능을 커버하면서, Unity 프로젝트에서 발생하는 의존성 관리 문제를 실용적으로 해결한다.
