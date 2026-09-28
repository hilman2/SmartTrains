using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SmartTrains.Core.Metrics;

namespace SmartTrains.Metrics
{
    /// <summary>
    /// The files of one metrics session: a folder named after the session,
    /// with one JSON Lines file per kind of record. Every record starts with
    /// the session's name ("s"), so that the files of many sessions can be
    /// read as one table.
    ///
    /// Lines go into a buffer and reach the disk once a second, so that a
    /// crash of the game loses at most the last second.
    /// </summary>
    internal sealed class MetricsLog : IDisposable
    {
        private static readonly Encoding kUtf8 = new UTF8Encoding(false);

        private readonly string m_Folder;
        private readonly Dictionary<string, StreamWriter> m_Files = new Dictionary<string, StreamWriter>();
        private readonly JsonLine m_Record = new JsonLine();
        private readonly Stopwatch m_SinceFlush = Stopwatch.StartNew();

        /// <summary>The session's name: when it began, local time, e.g. "20260928-031804".</summary>
        public string Session { get; }

        /// <summary>Opens a new session in a folder of its own under <paramref name="root"/>.</summary>
        public MetricsLog(string root)
        {
            string session = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string folder = Path.Combine(root, session);
            // Two cities loaded within one second.
            for (int i = 2; Directory.Exists(folder); i++)
                folder = Path.Combine(root, $"{session}-{i}");
            Directory.CreateDirectory(folder);
            m_Folder = folder;
            Session = Path.GetFileName(folder);
        }

        /// <summary>
        /// Starts a record with the session filled in. Add the fields and hand
        /// it to <see cref="Write"/> before starting the next one: all records
        /// share one builder.
        /// </summary>
        public JsonLine Record()
        {
            return m_Record.Begin().Add("s", Session);
        }

        /// <summary>Appends the record to <paramref name="file"/>.jsonl.</summary>
        public void Write(string file, JsonLine record)
        {
            if (!m_Files.TryGetValue(file, out StreamWriter writer))
            {
                writer = new StreamWriter(Path.Combine(m_Folder, file + ".jsonl"), true, kUtf8);
                m_Files[file] = writer;
            }
            writer.Write(record.End());
            writer.Write('\n');
        }

        /// <summary>Writes the buffered lines to disk if a second has passed since the last time, or now if <paramref name="now"/>.</summary>
        public void Flush(bool now)
        {
            if (!now && m_SinceFlush.ElapsedMilliseconds < 1000)
                return;
            foreach (StreamWriter writer in m_Files.Values)
                writer.Flush();
            m_SinceFlush.Restart();
        }

        public void Dispose()
        {
            foreach (StreamWriter writer in m_Files.Values)
                writer.Dispose();
            m_Files.Clear();
        }
    }
}
