// HangupEventHandler.csx
// FreeSWITCH mod_managed 事件回调示例（使用 EventConsumer）
// 用法1: 作为 API 在 CLI 中调用
//   managed HangupEventHandler status
//   managed HangupEventHandler start
// 用法2: 作为 App 在拨号计划中调用
//   <action application="managed" data="HangupEventHandler param1 param2 param3"/>
// 用法3: 直接加载
//   managedreload HangupEventHandler.csx

using System;
using System.Threading;
using FreeSWITCH;
using FreeSWITCH.Native;

/// <summary>
/// 挂断事件处理器 - 同时支持 API、App 和自动加载
/// </summary>
public class HangupEventHandler : IApiPlugin, IAppPlugin, ILoadNotificationPlugin
{
    private static Thread eventThread;
    private static bool isRunning = false;
    private static readonly object lockObj = new object();

    // 配置参数
    private static string logPrefix = "HangupHandler";
    private static bool enableDetailedLog = true;
    private static string customData = "";

    /// <summary>
    /// 模块加载时调用（ILoadNotificationPlugin）
    /// </summary>
    public bool Load()
    {
        try
        {
            Log.WriteLine(LogLevel.Info, "=== HangupEventHandler: 开始初始化 ===");

            // 自动启动事件监听线程
            StartEventListener();

            Log.WriteLine(LogLevel.Info, "=== HangupEventHandler: 初始化成功 ===");
            return true;
        }
        catch (Exception ex)
        {
            Log.WriteLine(LogLevel.Error, "HangupEventHandler Load 失败: {0}", ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// API 执行方法（IApiPlugin）
    /// 在 CLI 中调用: managed HangupEventHandler [action] [args...]
    /// </summary>
    public void Execute(FreeSWITCH.ApiContext context)
    {
        try
        {
            string args = (context.Arguments ?? "").Trim();

            if (string.IsNullOrEmpty(args))
            {
                WriteApiHelp(context.Stream);
                return;
            }

            string[] parts = args.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string action = parts[0].ToLower();

            switch (action)
            {
                case "start":
                    StartEventListener();
                    context.Stream.Write("+OK 事件监听器已启动\n");
                    break;

                case "stop":
                    StopEventListener();
                    context.Stream.Write("+OK 事件监听器已停止\n");
                    break;

                case "restart":
                    StopEventListener();
                    Thread.Sleep(1000);
                    StartEventListener();
                    context.Stream.Write("+OK 事件监听器已重启\n");
                    break;

                case "status":
                    WriteStatus(context.Stream);
                    break;

                case "config":
                    ConfigureFromArgsApi(parts, context.Stream);
                    break;

                case "help":
                    WriteApiHelp(context.Stream);
                    break;

                default:
                    context.Stream.Write(string.Format("-ERR 未知操作: {0}\n", action));
                    WriteApiHelp(context.Stream);
                    break;
            }
        }
        catch (Exception ex)
        {
            context.Stream.Write(string.Format("-ERR {0}\n", ex.Message));
            Log.WriteLine(LogLevel.Error, "[{0}] API Execute 异常: {1}", logPrefix, ex.ToString());
        }
    }

    /// <summary>
    /// API 后台执行方法（IApiPlugin）
    /// </summary>
    public void ExecuteBackground(FreeSWITCH.ApiBackgroundContext context)
    {
        try
        {
            string args = (context.Arguments ?? "").Trim();
            Log.WriteLine(LogLevel.Info, "[{0}] ExecuteBackground 被调用: {1}", logPrefix, args);

            if (args.ToLower() == "start")
            {
                StartEventListener();
            }
        }
        catch (Exception ex)
        {
            Log.WriteLine(LogLevel.Error, "[{0}] ExecuteBackground 异常: {1}", logPrefix, ex.ToString());
        }
    }

    /// <summary>
    /// App 执行方法（IAppPlugin）
    /// 在拨号计划中调用: <action application="managed" data="HangupEventHandler action [param1] [param2] ..."/>
    /// </summary>
    public void Run(FreeSWITCH.AppContext context)
    {
        try
        {
            string args = context.Arguments ?? "";
            ManagedSession session = context.Session;

            Log.WriteLine(LogLevel.Info, "[{0}] App Run 被调用，参数: {1}", logPrefix, args);

            // 解析参数
            string[] parts = args.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                session.Execute("log", string.Format("WARNING [{0}] 未提供参数", logPrefix));
                return;
            }

            string action = parts[0].ToLower();

            switch (action)
            {
                case "start":
                    StartEventListener();
                    session.Execute("log", string.Format("INFO [{0}] 事件监听器已启动", logPrefix));
                    break;

                case "stop":
                    StopEventListener();
                    session.Execute("log", string.Format("INFO [{0}] 事件监听器已停止", logPrefix));
                    break;

                case "status":
                    string status = isRunning ? "运行中" : "已停止";
                    string threadStatus = (eventThread != null && eventThread.IsAlive) ? "活动" : "非活动";
                    session.Execute("log", string.Format("INFO [{0}] 监听器: {1}, 线程: {2}", logPrefix, status, threadStatus));
                    break;

                case "config":
                    ConfigureFromArgsApp(parts, session);
                    break;

                case "monitor":
                    MonitorCall(parts, session);
                    break;

                case "setvars":
                    SetChannelVariables(parts, session);
                    break;

                case "sethook":
                    SetHangupHook(parts, session);
                    break;

                default:
                    session.Execute("log", string.Format("WARNING [{0}] 未知操作: {1}", logPrefix, action));
                    session.Execute("log", string.Format("INFO [{0}] 支持: start, stop, status, config, monitor, setvars, sethook", logPrefix));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.WriteLine(LogLevel.Error, "[{0}] App Run 异常: {1}", logPrefix, ex.ToString());
            context.Session.Execute("log", string.Format("ERROR [{0}] 执行异常: {1}", logPrefix, ex.Message));
        }
    }

    /// <summary>
    /// 输出 API 帮助信息
    /// </summary>
    private void WriteApiHelp(FreeSWITCH.Native.Stream stream)
    {
        stream.Write("HangupEventHandler - 通话挂断事件监听器\n");
        stream.Write("\n用法（API模式）:\n");
        stream.Write("  managed HangupEventHandler start              - 启动监听器\n");
        stream.Write("  managed HangupEventHandler stop               - 停止监听器\n");
        stream.Write("  managed HangupEventHandler restart            - 重启监听器\n");
        stream.Write("  managed HangupEventHandler status             - 查看状态\n");
        stream.Write("  managed HangupEventHandler config prefix=xxx detailed=true - 配置参数\n");
        stream.Write("  managed HangupEventHandler help               - 显示帮助\n");
        stream.Write("\n用法（App模式 - 在拨号计划中）:\n");
        stream.Write("  <action application=\"managed\" data=\"HangupEventHandler start\"/>\n");
        stream.Write("  <action application=\"managed\" data=\"HangupEventHandler setvars id=123 name=test\"/>\n");
        stream.Write("  <action application=\"managed\" data=\"HangupEventHandler monitor uuid=${uuid}\"/>\n");
    }

    /// <summary>
    /// 输出状态信息
    /// </summary>
    private void WriteStatus(FreeSWITCH.Native.Stream stream)
    {
        string status = isRunning ? "运行中" : "已停止";
        string threadStatus = (eventThread != null && eventThread.IsAlive) ? "活动" : "非活动";

        stream.Write(string.Format("监听器状态: {0}\n", status));
        stream.Write(string.Format("监听线程: {0}\n", threadStatus));
        stream.Write(string.Format("日志前缀: {0}\n", logPrefix));
        stream.Write(string.Format("详细日志: {0}\n", enableDetailedLog ? "启用" : "禁用"));
        if (!string.IsNullOrEmpty(customData))
        {
            stream.Write(string.Format("自定义数据: {0}\n", customData));
        }
    }

    /// <summary>
    /// 从 API 参数配置
    /// </summary>
    private void ConfigureFromArgsApi(string[] args, FreeSWITCH.Native.Stream stream)
    {
        for (int i = 1; i < args.Length; i++)
        {
            string[] pair = args[i].Split(new char[] { '=' }, 2);
            if (pair.Length != 2) continue;

            string key = pair[0].ToLower().Trim();
            string value = pair[1].Trim();

            switch (key)
            {
                case "prefix":
                    logPrefix = value;
                    stream.Write(string.Format("+OK 日志前缀已设置为: {0}\n", logPrefix));
                    break;

                case "detailed":
                    enableDetailedLog = value.ToLower() == "true" || value == "1";
                    stream.Write(string.Format("+OK 详细日志已{0}\n", enableDetailedLog ? "启用" : "禁用"));
                    break;

                case "custom":
                    customData = value;
                    stream.Write(string.Format("+OK 自定义数据已设置: {0}\n", customData));
                    break;

                default:
                    stream.Write(string.Format("-ERR 未知配置项: {0}\n", key));
                    break;
            }
        }
    }

    /// <summary>
    /// 从 App 参数配置
    /// </summary>
    private void ConfigureFromArgsApp(string[] args, ManagedSession session)
    {
        for (int i = 1; i < args.Length; i++)
        {
            string[] pair = args[i].Split(new char[] { '=' }, 2);
            if (pair.Length != 2) continue;

            string key = pair[0].ToLower().Trim();
            string value = pair[1].Trim();

            switch (key)
            {
                case "prefix":
                    logPrefix = value;
                    session.Execute("log", string.Format("INFO 日志前缀已设置为: {0}", logPrefix));
                    break;

                case "detailed":
                    enableDetailedLog = value.ToLower() == "true" || value == "1";
                    session.Execute("log", string.Format("INFO 详细日志已{0}", enableDetailedLog ? "启用" : "禁用"));
                    break;

                case "custom":
                    customData = value;
                    session.Execute("log", string.Format("INFO 自定义数据已设置: {0}", customData));
                    break;

                default:
                    session.Execute("log", string.Format("WARNING 未知配置项: {0}", key));
                    break;
            }
        }
    }

    /// <summary>
    /// 监控特定通话
    /// </summary>
    private void MonitorCall(string[] args, ManagedSession session)
    {
        string uuid = "";
        string caller = "";
        string callee = "";

        for (int i = 1; i < args.Length; i++)
        {
            string[] pair = args[i].Split(new char[] { '=' }, 2);
            if (pair.Length != 2) continue;

            string key = pair[0].ToLower().Trim();
            string value = pair[1].Trim();

            switch (key)
            {
                case "uuid":
                    uuid = value;
                    break;
                case "caller":
                    caller = value;
                    break;
                case "callee":
                    callee = value;
                    break;
            }
        }

        string logMsg = string.Format("INFO [{0}] 开始监控通话 - UUID: {1}, 主叫: {2}, 被叫: {3}",
            logPrefix, uuid, caller, callee);
        session.Execute("log", logMsg);

        // 在通道变量中保存监控标记
        session.SetVariable("hangup_monitor_enabled", "true");
        session.SetVariable("hangup_monitor_caller", caller);
        session.SetVariable("hangup_monitor_callee", callee);
    }

    /// <summary>
    /// 在通道上设置变量
    /// </summary>
    private void SetChannelVariables(string[] args, ManagedSession session)
    {
        for (int i = 1; i < args.Length; i++)
        {
            string[] pair = args[i].Split(new char[] { '=' }, 2);
            if (pair.Length != 2) continue;

            string key = pair[0].Trim();
            string value = pair[1].Trim();

            // 设置通道变量
            session.SetVariable(key, value);
            session.Execute("log", string.Format("INFO [{0}] 已设置通道变量: {1}={2}", logPrefix, key, value));
        }
    }

    /// <summary>
    /// 为当前通道设置挂断钩子
    /// </summary>
    private void SetHangupHook(string[] args, ManagedSession session)
    {
        // 设置挂断钩子标记
        session.SetVariable("execute_on_hangup", "log INFO [HangupHook] Channel hangup detected");

        // 可以传递自定义参数
        for (int i = 1; i < args.Length; i++)
        {
            string[] pair = args[i].Split(new char[] { '=' }, 2);
            if (pair.Length != 2) continue;

            string key = "hangup_hook_" + pair[0].Trim();
            string value = pair[1].Trim();
            session.SetVariable(key, value);
        }

        session.Execute("log", string.Format("INFO [{0}] 挂断钩子已设置", logPrefix));
    }

    /// <summary>
    /// 启动事件监听器
    /// </summary>
    private void StartEventListener()
    {
        lock (lockObj)
        {
            if (isRunning)
            {
                Log.WriteLine(LogLevel.Warning, "[{0}] 事件监听器已经在运行中", logPrefix);
                return;
            }

            try
            {
                isRunning = true;
                eventThread = new Thread(EventListenerThread);
                eventThread.IsBackground = true;
                eventThread.Start();

                Log.WriteLine(LogLevel.Info, "[{0}] 事件监听器线程已启动", logPrefix);
            }
            catch (Exception ex)
            {
                isRunning = false;
                Log.WriteLine(LogLevel.Error, "[{0}] 启动事件监听器失败: {1}", logPrefix, ex.ToString());
                throw;
            }
        }
    }

    /// <summary>
    /// 事件监听线程主循环
    /// </summary>
    private void EventListenerThread()
    {
        EventConsumer eventConsumer = null;

        try
        {
            Log.WriteLine(LogLevel.Info, "[{0}] 事件监听线程开始运行...", logPrefix);

            // 创建 EventConsumer 订阅 CHANNEL_HANGUP_COMPLETE 事件
            eventConsumer = new EventConsumer("CHANNEL_HANGUP_COMPLETE", "", 100);

            // 订阅 SHUTDOWN 事件以便优雅退出
            eventConsumer.bind("SHUTDOWN", "");

            Log.WriteLine(LogLevel.Info, "[{0}] 已订阅 CHANNEL_HANGUP_COMPLETE 和 SHUTDOWN 事件", logPrefix);

            // 主事件循环
            while (isRunning)
            {
                Event evt = eventConsumer.pop(1, 0);

                if (evt == null)
                {
                    continue;
                }

                try
                {
                    string eventName = evt.GetHeader("Event-Name");

                    if (eventName == "CHANNEL_HANGUP_COMPLETE")
                    {
                        OnHangupEvent(evt);
                    }
                    else if (eventName == "SHUTDOWN")
                    {
                        Log.WriteLine(LogLevel.Critical, "[{0}] 收到 SHUTDOWN 事件，停止监听器", logPrefix);
                        isRunning = false;
                        break;
                    }
                }
                finally
                {
                    if (evt != null)
                    {
                        evt.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.WriteLine(LogLevel.Error, "[{0}] 事件监听线程异常: {1}", logPrefix, ex.ToString());
        }
        finally
        {
            if (eventConsumer != null)
            {
                try
                {
                    eventConsumer.Dispose();
                    Log.WriteLine(LogLevel.Info, "[{0}] EventConsumer 已释放", logPrefix);
                }
                catch (Exception ex)
                {
                    Log.WriteLine(LogLevel.Error, "[{0}] 释放 EventConsumer 时出错: {1}", logPrefix, ex.Message);
                }
            }

            Log.WriteLine(LogLevel.Info, "[{0}] 事件监听线程已退出", logPrefix);
        }
    }

    /// <summary>
    /// 停止事件监听器
    /// </summary>
    private void StopEventListener()
    {
        lock (lockObj)
        {
            if (!isRunning)
            {
                return;
            }

            try
            {
                Log.WriteLine(LogLevel.Info, "[{0}] 正在停止事件监听器...", logPrefix);
                isRunning = false;

                if (eventThread != null && eventThread.IsAlive)
                {
                    if (!eventThread.Join(TimeSpan.FromSeconds(5)))
                    {
                        Log.WriteLine(LogLevel.Warning, "[{0}] 事件监听线程未能在5秒内退出", logPrefix);
                    }
                }

                eventThread = null;
                Log.WriteLine(LogLevel.Info, "[{0}] 事件监听器已停止", logPrefix);
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Error, "[{0}] 停止事件监听器失败: {1}", logPrefix, ex.ToString());
            }
        }
    }

    /// <summary>
    /// 获取事件头信息的辅助方法
    /// </summary>
    private string GetEventHeader(Event evt, string headerName)
    {
        try
        {
            string value = evt.GetHeader(headerName);
            return string.IsNullOrEmpty(value) ? "N/A" : value;
        }
        catch
        {
            return "N/A";
        }
    }

    /// <summary>
    /// 挂断事件回调处理函数
    /// </summary>
    private void OnHangupEvent(Event evt)
    {
        try
        {
            // 获取事件信息
            string uuid = GetEventHeader(evt, "Unique-ID");
            string callerIdNumber = GetEventHeader(evt, "Caller-Caller-ID-Number");
            string callerIdName = GetEventHeader(evt, "Caller-Caller-ID-Name");
            string destinationNumber = GetEventHeader(evt, "Caller-Destination-Number");
            string hangupCause = GetEventHeader(evt, "Hangup-Cause");
            string duration = GetEventHeader(evt, "variable_duration");
            string billsec = GetEventHeader(evt, "variable_billsec");
            string channelName = GetEventHeader(evt, "Channel-Name");
            string answerState = GetEventHeader(evt, "Answer-State");

            // 获取自定义通道变量
            string monitorEnabled = GetEventHeader(evt, "variable_hangup_monitor_enabled");
            string customTrackingId = GetEventHeader(evt, "variable_custom_tracking_id");
            string campaignName = GetEventHeader(evt, "variable_campaign_name");

            // 根据配置决定是否输出详细日志
            if (enableDetailedLog)
            {
                Log.WriteLine(LogLevel.Info, "╔════════════════════════════════════════════════════════════════");
                Log.WriteLine(LogLevel.Info, "║ [{0}] 通话挂断事件", logPrefix);
                Log.WriteLine(LogLevel.Info, "╠════════════════════════════════════════════════════════════════");
                Log.WriteLine(LogLevel.Info, "║ UUID:             {0}", uuid);
                Log.WriteLine(LogLevel.Info, "║ 主叫号码:         {0}", callerIdNumber);
                Log.WriteLine(LogLevel.Info, "║ 主叫名称:         {0}", callerIdName);
                Log.WriteLine(LogLevel.Info, "║ 被叫号码:         {0}", destinationNumber);
                Log.WriteLine(LogLevel.Info, "║ 挂断原因:         {0}", hangupCause);
                Log.WriteLine(LogLevel.Info, "║ 通话时长:         {0} 秒", duration);
                Log.WriteLine(LogLevel.Info, "║ 计费时长:         {0} 秒", billsec);
                Log.WriteLine(LogLevel.Info, "║ 通道名称:         {0}", channelName);
                Log.WriteLine(LogLevel.Info, "║ 应答状态:         {0}", answerState);

                if (monitorEnabled == "true")
                {
                    Log.WriteLine(LogLevel.Info, "║ [监控标记]        已启用");
                }
                if (customTrackingId != "N/A")
                {
                    Log.WriteLine(LogLevel.Info, "║ 跟踪ID:           {0}", customTrackingId);
                }
                if (campaignName != "N/A")
                {
                    Log.WriteLine(LogLevel.Info, "║ 活动名称:         {0}", campaignName);
                }
                if (!string.IsNullOrEmpty(customData))
                {
                    Log.WriteLine(LogLevel.Info, "║ 自定义数据:       {0}", customData);
                }

                Log.WriteLine(LogLevel.Info, "╚════════════════════════════════════════════════════════════════");
            }
            else
            {
                // 简洁日志
                Log.WriteLine(LogLevel.Info, "[{0}] 挂断 - UUID:{1} 主叫:{2} 被叫:{3} 原因:{4} 时长:{5}s",
                    logPrefix, uuid, callerIdNumber, destinationNumber, hangupCause, duration);
            }

            // 处理挂断原因
            ProcessHangupCause(hangupCause, uuid, callerIdNumber, destinationNumber);

        }
        catch (Exception ex)
        {
            Log.WriteLine(LogLevel.Error, "[{0}] 处理挂断事件时发生异常: {1}", logPrefix, ex.ToString());
        }
    }

    /// <summary>
    /// 根据挂断原因执行不同的处理逻辑
    /// </summary>
    private void ProcessHangupCause(string hangupCause, string uuid, string caller, string callee)
    {
        switch (hangupCause)
        {
            case "NORMAL_CLEARING":
                Log.WriteLine(LogLevel.Info, "[{0}] >>> 正常挂断: UUID={1}", logPrefix, uuid);
                break;

            case "USER_BUSY":
                Log.WriteLine(LogLevel.Warning, "[{0}] >>> 用户忙: 主叫={1}, 被叫={2}", logPrefix, caller, callee);
                break;

            case "NO_ANSWER":
                Log.WriteLine(LogLevel.Warning, "[{0}] >>> 无应答: 主叫={1}, 被叫={2}", logPrefix, caller, callee);
                break;

            case "CALL_REJECTED":
                Log.WriteLine(LogLevel.Warning, "[{0}] >>> 呼叫被拒绝: 主叫={1}, 被叫={2}", logPrefix, caller, callee);
                break;

            case "ORIGINATOR_CANCEL":
                Log.WriteLine(LogLevel.Info, "[{0}] >>> 主叫取消: 主叫={1}, 被叫={2}", logPrefix, caller, callee);
                break;

            default:
                Log.WriteLine(LogLevel.Notice, "[{0}] >>> 其他挂断原因 ({1}): UUID={2}", logPrefix, hangupCause, uuid);
                break;
        }
    }
}

// ===== 程序入口点 =====
public class Program
{
    public static void Main(string[] args)
    {
        // 满足编译器要求的入口点
    }
}