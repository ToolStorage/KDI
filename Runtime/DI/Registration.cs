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
    }
}
