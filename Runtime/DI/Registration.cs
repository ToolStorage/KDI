using System;

namespace Kylin.DI
{
    public enum Lifetime
    {
        Transient,  // 매번 새 인스턴스 생성
        Singleton,  // 단일 인스턴스 유지 (RootScope에서만)
        Scoped      // 해당 Scope 내 단일 인스턴스
    }

    public interface IDependencyObject { }

    public interface IInjectable { }

    public interface IPostInjectable
    {
        void PostInject();
    }

    public class Registration
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public object Instance { get; set; }
        public Lifetime Lifetime { get; set; }
        public Func<IScope, object> Factory { get; set; }

        public Func<object> Activator { get; set; }

        /// <summary>
        /// AlsoBind로 지정된 추가 서비스 타입들. 이 타입들은 ServiceType과 동일한 단일 인스턴스를 공유한다.
        /// </summary>
        public Type[] AliasTypes { get; set; }

        /// <summary>
        /// AsEntryPoint로 지정 시 스코프 빌드 시점에 즉시 인스턴스화된다(lazy resolve 우회).
        /// </summary>
        public bool IsEntryPoint { get; set; }
    }
}
