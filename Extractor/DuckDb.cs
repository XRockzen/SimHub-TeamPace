using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TeamPace.Extractor
{
    internal class DuckDbLockedException : Exception
    {
        public DuckDbLockedException(string message) : base(message) { }
    }

    /// <summary>Minimal read-only access to a DuckDB file through the C API of duckdb.dll.</summary>
    internal sealed class DuckDb : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Result
        {
            public ulong  ColumnCount, RowCount, RowsChanged;
            public IntPtr Columns, ErrorMessage, InternalData;
        }

        private const string Lib = "duckdb.dll";
        private const CallingConvention Cc = CallingConvention.Cdecl;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport(Lib, CallingConvention = Cc)] private static extern int  duckdb_create_config(out IntPtr config);
        [DllImport(Lib, CallingConvention = Cc)] private static extern int  duckdb_set_config(IntPtr config, byte[] name, byte[] option);
        [DllImport(Lib, CallingConvention = Cc)] private static extern void duckdb_destroy_config(ref IntPtr config);
        [DllImport(Lib, CallingConvention = Cc)] private static extern int  duckdb_open_ext(byte[] path, out IntPtr db, IntPtr config, out IntPtr error);
        [DllImport(Lib, CallingConvention = Cc)] private static extern void duckdb_close(ref IntPtr db);
        [DllImport(Lib, CallingConvention = Cc)] private static extern int  duckdb_connect(IntPtr db, out IntPtr con);
        [DllImport(Lib, CallingConvention = Cc)] private static extern void duckdb_disconnect(ref IntPtr con);
        [DllImport(Lib, CallingConvention = Cc)] private static extern int  duckdb_query(IntPtr con, byte[] sql, out Result result);
        [DllImport(Lib, CallingConvention = Cc)] private static extern IntPtr duckdb_result_error(ref Result result);
        [DllImport(Lib, CallingConvention = Cc)] private static extern void duckdb_destroy_result(ref Result result);
        [DllImport(Lib, CallingConvention = Cc)] private static extern ulong duckdb_row_count(ref Result result);
        [DllImport(Lib, CallingConvention = Cc)] private static extern IntPtr duckdb_value_varchar(ref Result result, ulong col, ulong row);
        [DllImport(Lib, CallingConvention = Cc)] private static extern void duckdb_free(IntPtr ptr);
        [DllImport(Lib, CallingConvention = Cc)] private static extern IntPtr duckdb_fetch_chunk(Result result);
        [DllImport(Lib, CallingConvention = Cc)] private static extern ulong duckdb_data_chunk_get_size(IntPtr chunk);
        [DllImport(Lib, CallingConvention = Cc)] private static extern IntPtr duckdb_data_chunk_get_vector(IntPtr chunk, ulong col);
        [DllImport(Lib, CallingConvention = Cc)] private static extern IntPtr duckdb_vector_get_data(IntPtr vector);
        [DllImport(Lib, CallingConvention = Cc)] private static extern void duckdb_destroy_data_chunk(ref IntPtr chunk);

        /// <summary>Loads the game's duckdb.dll so that the imports above bind to it.</summary>
        public static void Load(string dllPath)
        {
            const uint LoadWithAlteredSearchPath = 0x8;
            if (LoadLibraryEx(dllPath, IntPtr.Zero, LoadWithAlteredSearchPath) == IntPtr.Zero)
                throw new IOException("cannot load " + dllPath + " (error " + Marshal.GetLastWin32Error() + ")");
        }

        private IntPtr _db, _con;

        public DuckDb(string path)
        {
            IntPtr config;
            duckdb_create_config(out config);
            try
            {
                duckdb_set_config(config, Utf8("access_mode"), Utf8("READ_ONLY"));
                // Light on the machine: the extraction may run while the player drives the next stint
                duckdb_set_config(config, Utf8("threads"), Utf8("2"));
                duckdb_set_config(config, Utf8("memory_limit"), Utf8("1GB"));
                IntPtr error;
                if (duckdb_open_ext(Utf8(path), out _db, config, out error) != 0)
                {
                    var msg = error != IntPtr.Zero ? FromUtf8(error) : "open failed";
                    if (error != IntPtr.Zero) duckdb_free(error);
                    // The game keeps the recording of the current stint open
                    if (msg.IndexOf("lock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("being used", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new DuckDbLockedException(msg);
                    throw new IOException(msg);
                }
            }
            finally { duckdb_destroy_config(ref config); }
            if (duckdb_connect(_db, out _con) != 0)
            {
                duckdb_close(ref _db);
                throw new IOException("connect failed");
            }
        }

        public void Dispose()
        {
            if (_con != IntPtr.Zero) duckdb_disconnect(ref _con);
            if (_db  != IntPtr.Zero) duckdb_close(ref _db);
        }

        /// <summary>Small text results (metadata): rows of strings.</summary>
        public List<string[]> QueryText(string sql, int columns)
        {
            var rows = new List<string[]>();
            Result res = Run(sql);
            try
            {
                ulong n = duckdb_row_count(ref res);
                for (ulong r = 0; r < n; r++)
                {
                    var row = new string[columns];
                    for (int c = 0; c < columns; c++)
                    {
                        IntPtr p = duckdb_value_varchar(ref res, (ulong)c, r);
                        row[c] = p == IntPtr.Zero ? "" : FromUtf8(p);
                        if (p != IntPtr.Zero) duckdb_free(p);
                    }
                    rows.Add(row);
                }
            }
            finally { duckdb_destroy_result(ref res); }
            return rows;
        }

        /// <summary>Numeric results: every selected column must be cast to DOUBLE in the query.</summary>
        public double[][] QueryDoubles(string sql, int columns)
        {
            var cols = new List<double>[columns];
            for (int c = 0; c < columns; c++) cols[c] = new List<double>();
            Result res = Run(sql);
            try
            {
                var buffer = new double[2048];
                while (true)
                {
                    IntPtr chunk = duckdb_fetch_chunk(res);
                    if (chunk == IntPtr.Zero) break;
                    try
                    {
                        int size = (int)duckdb_data_chunk_get_size(chunk);
                        if (size == 0) break;
                        if (buffer.Length < size) buffer = new double[size];
                        for (int c = 0; c < columns; c++)
                        {
                            IntPtr data = duckdb_vector_get_data(duckdb_data_chunk_get_vector(chunk, (ulong)c));
                            Marshal.Copy(data, buffer, 0, size);
                            for (int i = 0; i < size; i++) cols[c].Add(buffer[i]);
                        }
                    }
                    finally { duckdb_destroy_data_chunk(ref chunk); }
                }
            }
            finally { duckdb_destroy_result(ref res); }

            var outCols = new double[columns][];
            for (int c = 0; c < columns; c++) outCols[c] = cols[c].ToArray();
            return outCols;
        }

        private Result Run(string sql)
        {
            Result res;
            if (duckdb_query(_con, Utf8(sql), out res) != 0)
            {
                IntPtr e = duckdb_result_error(ref res);
                var msg = e != IntPtr.Zero ? FromUtf8(e) : "query failed";
                duckdb_destroy_result(ref res);
                throw new IOException(msg);
            }
            return res;
        }

        private static byte[] Utf8(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);   // NUL terminator
            return b;
        }

        private static string FromUtf8(IntPtr p)
        {
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            var b = new byte[len];
            Marshal.Copy(p, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }
    }
}
