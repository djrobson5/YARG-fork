using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using YARG.Core.Engine;

namespace YARG.RewindSimBench
{
    /// <summary>
    /// Every public value-typed property on the engine and on its stats object, captured by
    /// reflection so that nothing is missed by a hand-written field list.
    /// </summary>
    public sealed class Snapshot
    {
        public readonly Dictionary<string, string> Values = new();

        public static Snapshot Capture(BaseEngine engine)
        {
            var snap = new Snapshot();

            Collect(snap, "engine", engine);
            Collect(snap, "stats", engine.BaseStats);

            return snap;
        }

        private static void Collect(Snapshot snap, string prefix, object target)
        {
            foreach (var field in target.GetType()
                         .GetFields(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(f => f.Name))
            {
                var fieldType = field.FieldType;
                if (!fieldType.IsPrimitive && !fieldType.IsEnum && fieldType != typeof(decimal))
                {
                    continue;
                }

                snap.Values[$"{prefix}.{field.Name}"] = Format(field.GetValue(target));
            }

            foreach (var prop in target.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(p => p.Name))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var type = prop.PropertyType;
                if (!type.IsPrimitive && !type.IsEnum && type != typeof(decimal))
                {
                    continue;
                }

                object value;
                try
                {
                    value = prop.GetValue(target);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                snap.Values[$"{prefix}.{prop.Name}"] = Format(value);
            }
        }

        private static string Format(object value) => value switch
        {
            null    => "<null>",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f  => f.ToString("R", CultureInfo.InvariantCulture),
            _        => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

        /// <summary>Returns the differing keys, as (key, left, right).</summary>
        public static List<(string Key, string Left, string Right)> Diff(Snapshot a, Snapshot b)
        {
            var diffs = new List<(string, string, string)>();

            foreach (var key in a.Values.Keys.Concat(b.Values.Keys).Distinct().OrderBy(k => k))
            {
                a.Values.TryGetValue(key, out var left);
                b.Values.TryGetValue(key, out var right);

                if (left != right)
                {
                    diffs.Add((key, left ?? "<absent>", right ?? "<absent>"));
                }
            }

            return diffs;
        }
    }
}
