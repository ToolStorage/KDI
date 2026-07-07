using System.Runtime.CompilerServices;

// 에디터 인스펙터(LifetimeScopeEditor)가 Scope의 등록 목록을 읽기 전용으로 접근하기 위한 예외.
// 런타임 코드는 에디터 어셈블리를 참조하지 않는다 (단방향).
[assembly: InternalsVisibleTo("Kylin.DI.Editor")]
