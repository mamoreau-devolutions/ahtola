namespace Ahtola.Core.Parsing;

/// <summary>
/// Performs the surgical text edits that <c>ALTER TABLE ... ADD COLUMN</c> and
/// <c>ALTER TABLE ... DROP COLUMN</c> apply to the stored CREATE TABLE statement. SQLite keeps
/// the original statement text and edits tokens in place (sqlite3AlterAddColumn /
/// sqlite3AlterDropColumn in alter.c), so verbatim spacing, quoting, and comments survive.
/// Ahtola falls back to regenerating the text only when the stored text cannot be reparsed.
/// </summary>
internal static class AlterTableSqlRewriter
{
    /// <summary>
    /// Inserts the added column definition immediately after the last existing column
    /// definition, the way SQLite's token edit does: a table constraint may only follow column
    /// definitions, so the new column lands before the first table constraint (or before the
    /// closing parenthesis when there is none). Returns <see langword="null"/> when the stored
    /// text cannot be reparsed.
    /// </summary>
    public static string? InsertAddedColumn(string sql, string columnSql)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        if (parsed is not CreateTableStatement statement)
            return null;
        if (spans.GetQualifier(statement) is not { } closeParen)
            return null;

        // When table constraints follow the columns, the last column's recorded extent ends
        // right after its separating comma; insert "column, " there to keep constraints last.
        if (statement.Columns.Count > 0
            && spans.GetDefinitionExtent(statement.Columns[^1]) is { } lastColumnExtent
            && lastColumnExtent.End < closeParen.Start)
        {
            return string.Concat(
                sql.AsSpan(0, lastColumnExtent.End),
                columnSql,
                ", ",
                sql.AsSpan(lastColumnExtent.End));
        }

        return string.Concat(
            sql.AsSpan(0, closeParen.Start),
            ", ",
            columnSql,
            sql.AsSpan(closeParen.Start));
    }

    /// <summary>
    /// Removes the stored definition of <paramref name="columnName"/> including exactly one
    /// separating comma, the way SQLite's DROP COLUMN edit does. Returns <see langword="null"/>
    /// when the stored text cannot be reparsed or the definition extent is unknown.
    /// </summary>
    public static string? RemoveColumn(string sql, string columnName)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        if (parsed is not CreateTableStatement statement)
            return null;

        var column = statement.Columns.FirstOrDefault(
            candidate => string.Equals(candidate.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null)
            return null;
        if (spans.GetDefinitionExtent(column) is not { } extent)
            return null;

        return string.Concat(sql.AsSpan(0, extent.Start), sql.AsSpan(extent.End));
    }

    /// <summary>
    /// Replaces the table-name token in the stored CREATE TABLE text. SQLite's RENAME TO edit
    /// always writes the new name double-quoted (sqlite3_rename_token in alter.c), so the
    /// replacement is quoted unconditionally. Returns <see langword="null"/> when the stored text
    /// cannot be reparsed.
    /// </summary>
    public static string? RenameTable(string sql, string newName)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        SqlSourceSpan? nameSpan = parsed switch
        {
            CreateTableStatement statement => spans.GetName(statement),
            CreateTableAsSelectStatement statement => spans.GetName(statement),
            _ => null,
        };
        if (nameSpan is not { } name)
            return null;

        return ReplaceSpan(sql, name, QuoteIdentifier(newName));
    }

    /// <summary>
    /// Rewrites the ON-clause table reference of a stored CREATE INDEX statement. Returns
    /// <see langword="null"/> when the stored text cannot be reparsed.
    /// </summary>
    public static string? RenameIndexTable(string sql, string newName)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        if (parsed is not CreateIndexStatement statement)
            return null;
        if (spans.GetQualifier(statement) is not { } tableName)
            return null;

        return ReplaceSpan(sql, tableName, QuoteIdentifier(newName));
    }

    /// <summary>
    /// Rewrites every foreign-key reference to <paramref name="oldParentName"/> in a stored
    /// CREATE TABLE statement so it points at <paramref name="newName"/>. Returns
    /// <see langword="null"/> when the stored text cannot be reparsed or any matching reference
    /// lacks a recorded token span.
    /// </summary>
    public static string? RenameForeignKeyParentTable(string sql, string oldParentName, string newName)
    {
        ParsedStatement parsed;
        SqlSourceSpans spans;
        try
        {
            parsed = SqlParser.ParseWithSpans(sql, out spans);
        }
        catch (EmbeddedSqlException)
        {
            return null;
        }

        if (parsed is not CreateTableStatement statement)
            return null;

        var foreignKeys = statement.Columns
            .Where(column => column.ForeignKey is not null)
            .Select(column => column.ForeignKey!)
            .Concat(statement.TableForeignKeys ?? []);

        var targetSpans = new List<SqlSourceSpan>();
        foreach (var foreignKey in foreignKeys)
        {
            if (!string.Equals(foreignKey.ParentTable, oldParentName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (spans.GetName(foreignKey) is not { } parentSpan)
                return null;

            targetSpans.Add(parentSpan);
        }

        if (targetSpans.Count == 0)
            return sql;

        var replacement = QuoteIdentifier(newName);
        foreach (var span in targetSpans.OrderByDescending(span => span.Start))
            sql = ReplaceSpan(sql, span, replacement);

        return sql;
    }

    private static string ReplaceSpan(string sql, SqlSourceSpan span, string replacement)
        => string.Concat(sql.AsSpan(0, span.Start), replacement, sql.AsSpan(span.End));

    private static string QuoteIdentifier(string name)
        => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
