using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmartTrains.Core.Metrics
{
    /// <summary>
    /// Builds one flat JSON object as a single line, for the metrics files:
    /// one record per line (JSON Lines), which DuckDB and most tools read
    /// directly and which survive a crash up to the last complete line.
    /// </summary>
    public sealed class JsonLine
    {
        private readonly StringBuilder m_Text = new StringBuilder(256);
        private bool m_Empty = true;

        /// <summary>Starts a new object, dropping whatever was built before.</summary>
        public JsonLine Begin()
        {
            m_Text.Clear();
            m_Text.Append('{');
            m_Empty = true;
            return this;
        }

        /// <summary>The object as text, without a line break.</summary>
        public string End()
        {
            m_Text.Append('}');
            return m_Text.ToString();
        }

        /// <summary>A string field; null is written as JSON null.</summary>
        public JsonLine Add(string name, string value)
        {
            Name(name);
            if (value == null)
                m_Text.Append("null");
            else
                Quote(value);
            return this;
        }

        public JsonLine Add(string name, long value)
        {
            Name(name);
            m_Text.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// A number field with at most three decimals, which is finer than
        /// anything measured here: metres, seconds, milliseconds. NaN and
        /// infinity, which JSON cannot hold, are written as null.
        /// </summary>
        public JsonLine Add(string name, double value)
        {
            Name(name);
            if (double.IsNaN(value) || double.IsInfinity(value))
                m_Text.Append("null");
            else
                m_Text.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLine Add(string name, bool value)
        {
            Name(name);
            m_Text.Append(value ? "true" : "false");
            return this;
        }

        public JsonLine Add(string name, IEnumerable<long> values)
        {
            Name(name);
            m_Text.Append('[');
            bool first = true;
            foreach (long value in values)
            {
                if (!first)
                    m_Text.Append(',');
                m_Text.Append(value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }
            m_Text.Append(']');
            return this;
        }

        private void Name(string name)
        {
            if (!m_Empty)
                m_Text.Append(',');
            m_Empty = false;
            Quote(name);
            m_Text.Append(':');
        }

        /// <summary>
        /// Writes a JSON string. Quotes, backslashes and control characters
        /// are escaped; everything else, e.g. umlauts in names, goes in as it
        /// is, since the files are written as UTF-8.
        /// </summary>
        private void Quote(string value)
        {
            m_Text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        m_Text.Append("\\\"");
                        break;
                    case '\\':
                        m_Text.Append("\\\\");
                        break;
                    case '\n':
                        m_Text.Append("\\n");
                        break;
                    case '\r':
                        m_Text.Append("\\r");
                        break;
                    case '\t':
                        m_Text.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            m_Text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            m_Text.Append(c);
                        break;
                }
            }
            m_Text.Append('"');
        }
    }
}
