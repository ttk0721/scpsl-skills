using System.ComponentModel;
using System.Collections.Generic;
using UnityEngine;

namespace Fixture
{
    public class PluginConfig
    {
        [Description("Where it spawns.")]
        public Vector3 Spawn { get; set; } = Vector3.zero;

        public Vector4 Tint { get; set; } = new Vector4(1, 1, 1, 1); // expect: SL014

        public List<Vector4> Tints { get; set; } = new List<Vector4>(); // expect: SL014

        [YamlDotNet.Serialization.YamlIgnore]
        public Vector4 Runtime { get; set; }

        private Vector4 cache { get; set; }

        public Vector4 Compute() { return default; }

        public Color Color { get; set; }
    }

    public class NestedHolder
    {
        public class Settings
        {
            public Vector4 Inner { get; set; } // expect: SL014
        }
    }

    // not a config class: no Description, name does not look like one, not Plugin<T>
    public class Geometry
    {
        public Vector4 Plane { get; set; }
    }

    public class Marked
    {
        [Description("explicit config marker")]
        public int Port { get; set; }

        public Vector4 Marker { get; set; } // expect: SL014
    }
}
