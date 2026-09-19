using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MC.Log.M
{
    public class LogProperty
    {
        public string Name { get; }
        public object Value { get; }

        public LogProperty(string name, object value)
        {
            Name = name;
            Value = value;
        }

        public static implicit operator LogProperty(
            (string name, object value) value)
        {
            return new LogProperty(value.name, value.value);
        }
    }
}
