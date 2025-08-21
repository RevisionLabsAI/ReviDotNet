using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Revi;

public static class TableParserExtensions
{
    public static string ToStringTable<T>(this IEnumerable<T> values, string[] columnHeaders, params Func<T, object>[] valueSelectors)
    {
        return ToStringTable(values?.ToArray(), columnHeaders, valueSelectors);
    }

    public static string ToStringTable<T>(this T[] values, string[] columnHeaders, params Func<T, object>[] valueSelectors)
    {
        Debug.Assert(columnHeaders != null && valueSelectors != null);
        if (values == null || columnHeaders.Length != valueSelectors.Length)
        {
            return string.Empty;
        }

        var arrValues = new string[values.Length + 1, valueSelectors.Length];

        // Fill headers
        for (int colIndex = 0; colIndex < arrValues.GetLength(1); colIndex++)
        {
            arrValues[0, colIndex] = columnHeaders[colIndex] ?? string.Empty;
        }

        // Fill table rows
        for (int rowIndex = 1; rowIndex < arrValues.GetLength(0); rowIndex++)
        {
            for (int colIndex = 0; colIndex < arrValues.GetLength(1); colIndex++)
            {
                var valueSelector = valueSelectors[colIndex];
                object value = valueSelector?.Invoke(values[rowIndex - 1]);

                arrValues[rowIndex, colIndex] = value?.ToString() ?? "null";
            }
        }

        return ToStringTable(arrValues);
    }

    public static string ToStringTable(this string[,] arrValues)
    {
        if (arrValues == null)
        {
            return string.Empty;
        }

        int[] maxColumnsWidth = GetMaxColumnsWidth(arrValues);
        var headerSplitter = new string('-', maxColumnsWidth.Sum(i => i + 3) - 1);

        var sb = new StringBuilder();
        for (int rowIndex = 0; rowIndex < arrValues.GetLength(0); rowIndex++)
        {
            for (int colIndex = 0; colIndex < arrValues.GetLength(1); colIndex++)
            {
                // Print cell
                var cell = (arrValues[rowIndex, colIndex] ?? string.Empty).PadRight(maxColumnsWidth[colIndex]);
                sb.Append(" | ");
                sb.Append(cell);
            }

            // Print end of line
            sb.Append(" | ");
            sb.AppendLine();

            // Print splitter
            if (rowIndex == 0)
            {
                sb.AppendFormat(" |{0}| ", headerSplitter);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static int[] GetMaxColumnsWidth(string[,] arrValues)
    {
        var maxColumnsWidth = new int[arrValues.GetLength(1)];
        for (int colIndex = 0; colIndex < arrValues.GetLength(1); colIndex++)
        {
            for (int rowIndex = 0; rowIndex < arrValues.GetLength(0); rowIndex++)
            {
                int newLength = (arrValues[rowIndex, colIndex] ?? string.Empty).Length;
                int oldLength = maxColumnsWidth[colIndex];

                if (newLength > oldLength)
                {
                    maxColumnsWidth[colIndex] = newLength;
                }
            }
        }

        return maxColumnsWidth;
    }

    public static string ToStringTable<T>(this IEnumerable<T> values, params Expression<Func<T, object>>[] valueSelectors)
    {
        if (values == null || valueSelectors == null)
        {
            return string.Empty;
        }

        var headers = valueSelectors.Select(func => GetProperty(func)?.Name ?? string.Empty).ToArray();
        var selectors = valueSelectors.Select(exp => exp.Compile()).ToArray();
        return ToStringTable(values, headers, selectors);
    }

    private static PropertyInfo GetProperty<T>(Expression<Func<T, object>> expression)
    {
        if (expression.Body is UnaryExpression unaryExpression)
        {
            if (unaryExpression.Operand is MemberExpression memberExpression)
            {
                return memberExpression.Member as PropertyInfo;
            }
        }

        if (expression.Body is MemberExpression memberExp)
        {
            return memberExp.Member as PropertyInfo;
        }
        return null;
    }
}