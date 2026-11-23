using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace NewsDesk.Infrastructure.Storage;

internal static class SqlHelpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string? Iso(DateTimeOffset? d) => d?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static SqliteCommand Cmd(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                Guid g => g.ToString(),
                bool b => b ? 1 : 0,
                DateTimeOffset d => Iso(d),
                Enum e => e.ToString(),
                _ => value,
            });
        return cmd;
    }

    public static int Exec(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.Cmd(sql, tx, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string S(this SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));
    public static string? NS(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
    public static int I(this SqliteDataReader r, string col) => r.GetInt32(r.GetOrdinal(col));
    public static bool B(this SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col)) != 0;
    public static Guid G(this SqliteDataReader r, string col) => Guid.Parse(r.GetString(r.GetOrdinal(col)));
    public static T E<T>(this SqliteDataReader r, string col) where T : struct, Enum => Enum.Parse<T>(r.GetString(r.GetOrdinal(col)));

    public static DateTimeOffset? ND(this SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static DateTimeOffset D(this SqliteDataReader r, string col) => r.ND(col)!.Value;
}
