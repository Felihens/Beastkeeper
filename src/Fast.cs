using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace Beastkeeper
{
    // Direct field access for code that runs for every creature, every frame (the AI patches).
    // FieldInfo.GetValue is reflection on every call; these are compiled once into a plain field read.
    internal static class Fast
    {
        internal static readonly Func<BaseAI, Character> AIChar = Getter<BaseAI, Character>("m_character");
        internal static readonly Func<BaseAI, ZNetView> AIView = Getter<BaseAI, ZNetView>("m_nview");

        private static Func<T, R> Getter<T, R>(string field)
        {
            FieldInfo f = AccessTools.Field(typeof(T), field);
            try
            {
                ParameterExpression o = Expression.Parameter(typeof(T), "o");
                return Expression.Lambda<Func<T, R>>(Expression.Field(o, f), o).Compile();
            }
            catch (Exception) { return x => x != null ? (R)f.GetValue(x) : default(R); }
        }
    }
}
