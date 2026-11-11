using System;

namespace QoL
{
    /// <summary>
    /// `ModReflect` 的安全包装：它找不到成员时会抛异常，而原来的 `AccessTools.Method/Field`
    /// 是返回 null。这里的每个方法都把异常吃掉、返回 null / 默认值，
    /// 让调用处原来的「找不到就跳过」逻辑照常工作。
    ///
    /// 用 `ModReflect` 而不是 `AccessTools` 的原因：后者算 reflection-external，
    /// 游戏从 2026-12-10 起会拒绝这类工坊模组；`ModReflect` 是游戏提供的受控通道，不算。
    /// </summary>
    internal static class ReflectSafe
    {
        internal static System.Reflection.MethodBase Target(Type type, string name, Type[] signature = null)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            try
            {
                return ModReflect.Target(type, name, signature);
            }
            catch (Exception e)
            {
                QLog.Warn("[反射] 找不到 " + type.Name + "." + name + "：" + e.Message);
                return null;
            }
        }

        internal static bool Has(object target, string member)
        {
            try
            {
                return ModReflect.Has(target, member);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读成员；类型对不上或读不到时返回 fallback。</summary>
        internal static T Get<T>(object target, string member, T fallback = default(T))
        {
            if (target == null || string.IsNullOrEmpty(member))
            {
                return fallback;
            }

            try
            {
                object value = ModReflect.Get(target, member);
                return value is T typed ? typed : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        internal static bool Set(object target, string member, object value)
        {
            if (target == null || string.IsNullOrEmpty(member))
            {
                return false;
            }

            try
            {
                ModReflect.Set(target, member, value);
                return true;
            }
            catch (Exception e)
            {
                QLog.Warn("[反射] 写 " + member + " 失败：" + e.Message);
                return false;
            }
        }
    }
}
