using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>解释器服务契约（PRD §8.2）。</summary>
public interface IInterpreterService
{
    /// <summary>登记解释器并探测版本（exe --version，3 秒超时）。路径已存在则更新版本信息。</summary>
    Interpreter AddFromPath(string exePath);

    /// <summary>调用 exe --version 解析版本号；超时/失败返回空字符串。</summary>
    string DetectVersion(string exePath);

    IReadOnlyList<Interpreter> GetAll();
    Interpreter? GetDefault();

    void SetDefault(int id);

    /// <summary>删除；若仍被 Script 引用则抛 InvalidOperationException（Error_InterpreterInUse）。</summary>
    void Delete(int id);

    /// <summary>自动扫描候选解释器路径（PythonLocator 同款 PATH 逻辑 + py -0 列举）。</summary>
    IReadOnlyList<string> ScanCandidates();
}
