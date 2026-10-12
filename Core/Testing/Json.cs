using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DrivenRides.Core.Testing
{
    internal sealed class Json
    {
        private readonly StringBuilder text = new StringBuilder("{");
        private bool first = true;

        public Json Field(string name, object? value)
        {
            if (!first) text.Append(',');
            first = false;
            String(name);
            text.Append(':');
            Value(value);
            return this;
        }

        private void Value(object? value)
        {
            if (value == null) text.Append("null");
            else if (value is string s) String(s);
            else if (value is bool b) text.Append(b ? "true" : "false");
            else if (value is IEnumerable<int> ints)
            {
                text.Append('[');
                bool comma = false;
                foreach (int i in ints) { if (comma) text.Append(','); comma = true; Value(i); }
                text.Append(']');
            }
            else if (value is IEnumerable<string> strings)
            {
                text.Append('[');
                bool comma = false;
                foreach (string item in strings) { if (comma) text.Append(','); comma = true; String(item); }
                text.Append(']');
            }
            else if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) text.Append("null");
            else text.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private void String(string value)
        {
            text.Append('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            text.Append('"');
        }

        public override string ToString() => text.ToString() + "}";
    }
}
