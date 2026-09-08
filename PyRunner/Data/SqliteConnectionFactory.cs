using Microsoft.Data.Sqlite;

namespace PyRunner.Data;

/// <summary>
/// SQLite 连接工厂：库路径 %LOCALAPPDATA%\PyRunner\pyrunner.db（目录不存在则创建），
/// 每个连接启用 PRAGMA synchronous=NORMAL 与 foreign_keys=ON；
/// journal_mode=WAL 由首个连接持久化到库文件。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private const string DataDirectoryEnvironmentVariable = "PYRUNNER_DATA_DIRECTORY";
    private readonly object _initLock = new();
    private bool _walEnsured;

    public SqliteConnectionFactory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        DatabaseDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyRunner")
            : Path.GetFullPath(overrideDirectory);
    }

    /// <summary>验证工具专用：将数据库隔离到临时目录。</summary>
    internal SqliteConnectionFactory(string databaseDirectory)
    {
        DatabaseDirectory = databaseDirectory;
    }

    public string DatabaseDirectory { get; }

    public string DatabasePath => Path.Combine(DatabaseDirectory, "pyrunner.db");

    /// <summary>数据库文件被恢复流程替换后，允许下一连接重新初始化 WAL。</summary>
    public void ResetInitialization()
    {
        lock (_initLock) { _walEnsured = false; }
    }

    /// <summary>创建并打开一个已应用 PRAGMA 的连接。</summary>
    public SqliteConnection CreateOpenConnection()
    {
        Directory.CreateDirectory(DatabaseDirectory);

        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        // WAL 持久化于库文件头，首次连接设置一次即可（事务外）
        lock (_initLock)
        {
            if (!_walEnsured)
            {
                using var walCmd = connection.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                walCmd.ExecuteNonQuery();
                _walEnsured = true;
            }
        }

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragmaCmd.ExecuteNonQuery();

        return connection;
    }
}
