namespace MBScript.Models;

public class SqlResult<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public int RowsAffected { get; set; }
    public string? Error { get; set; }

    public static SqlResult<T> Ok(T data, int rowsAffected = 0) =>
        new() { Success = true, Data = data, RowsAffected = rowsAffected };

    public static SqlResult<T> Fail(string error) =>
        new() { Success = false, Error = error };
}
