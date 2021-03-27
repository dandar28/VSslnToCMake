using System;
using System.Collections.Generic;
using System.Linq;

namespace CMakeWriter.CMProject
{
    public static class GeneratorExpression
    {
        public static string ConfigExpression<T, S>(
            T cfg, S value, Func<T, String> predCfg, Func<S, String> predValue)
        {
            return string.Format("$<$<CONFIG:{0}>:{1}>",
                                 predCfg(cfg), predValue(value));
        }

        public static string ConfigExpression<T>(
            T t, Func<T, String> predCfg, Func<T, String> predValue)
        {
            return ConfigExpression<T, T>(t, t, predCfg, predValue);
        }

        public static string ConfigExpressions<T>(
             this IEnumerable<T> source, string separator,
             Func<T, string> predCfg, Func<T, string> predValue)
        {
            var cfgs = source
                       .Where(x => predValue(x) != "")
                       .Select(x => ConfigExpression(x, predCfg, predValue))
                       .ToArray();
            if (cfgs.Count() == 0)
            {
                return "";
            }
            return cfgs.Aggregate((a, b) => a + separator + b);
        }
    }
}
