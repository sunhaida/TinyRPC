using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using zFramework.TinyRPC.DataModel;
using zFramework.TinyRPC.Settings;

namespace zFramework.TinyRPC
{
    // 获取所有的消息处理器解析并缓存
    // 消息处理器是被 MessageHandlerAttribute 标记的方法
    // 约定 MessageHandlerAttribute 只会出现在静态方法上
    public static class MessageManager
    {
        static readonly Dictionary<Type, NormalHandlerInfo> normalHandlers = new();
        static readonly Dictionary<Type, RpcHandlerInfo> rpcHandlers = new();
        static readonly Dictionary<Type, Type> rpcMessagePairs = new(); // RPC 消息对，key = Request , value = Response
        static readonly Dictionary<int, RpcInfo> rpcInfoPairs = new(); // RpcId + RpcInfo

        //通过反射获取所有的RPC消息映射
        // 约定消息必须存在于同一个叫做 ：com.zframework.tinyrpc.generate 程序集中
        // 此程序集将根据 proto 文件描述的继承关系一键生成
        // 计划将其放在 Packages 文件夹
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        public static void Awake()
        {
            rpcInfoPairs.Clear();
            normalHandlers.Clear();
            rpcHandlers.Clear();
            Debug.Log($"{nameof(MessageManager)}: awake ~");
            // 如果不是双向 rpc ,应该需要一个 Enable Server 的宏，这样避免客户端获取 RPC Pairs
            StoreRPCMessagePairs();
            RegisterAllHandlers();
        }

        public static void StoreRPCMessagePairs()
        {
            rpcMessagePairs.Clear();
            // add ping message internal
            rpcMessagePairs.Add(typeof(Ping), typeof(Ping));
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(v => v.FullName.StartsWith("com.zframework.tinyrpc.generated"));

            if (assembly != null)
            {
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    if (type.IsSubclassOf(typeof(Request)))
                    {
                        var attr = type.GetCustomAttribute<ResponseTypeAttribute>();
                        if (attr != null)
                        {
                            rpcMessagePairs.Add(type, attr.Type);
                        }
                        else
                        {
                            Debug.LogError($"{nameof(MessageManager)}: 请务必为 {type.Name} 通过 ResponseTypeAttribute 配置 Response 消息！");
                        }
                    }
                }
            }
            else
            {
                Debug.Log($"{nameof(MessageManager)}: 请保证 生成的网络消息在 “com.zframework.tinyrpc.generated” 程序集下");
            }
            foreach (var item in rpcMessagePairs)
            {
                Debug.Log($"{nameof(MessageManager)}: RPC Pair Added , request = {item.Key.Name}, response = {item.Value.Name}");
            }
        }

        public static void RegisterAllHandlers()
        {
            // store ping message handler internal
            RegisterHandler(typeof(TCPServer));

            // store all message handlers
            var handlers = AppDomain.CurrentDomain.GetAssemblies()
                .Where(v => TinyRpcSettings.Instance.AssemblyNames.Contains(v.FullName.Split(',')[0]))
                .SelectMany(v => v.GetTypes())
                .Where(v => v.GetCustomAttribute<MessageHandlerProviderAttribute>() != null);
            foreach (var handler in handlers)
            {
                RegisterHandler(handler);
            }
            Debug.Log($"{nameof(MessageManager)}:  rpc handles count = {rpcHandlers.Count()}");
            foreach (var item in rpcHandlers)
            {
                Debug.Log($"{nameof(MessageManager)}:  for {item.Key.Name} - handler = {item.Value.method.Name}");
            }
        }

        public static void RegisterHandler(Type type)
        {
            // 网络消息处理器必须是静态方法
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(v => v.GetCustomAttribute<MessageHandlerAttribute>() != null);
            foreach (var method in methods)
            {
                //log error if method is not static
                if (!method.IsStatic)
                {
                    Debug.LogError($"MessageHandler {method.DeclaringType.Name}.{method.Name} 必须是静态方法！");
                    continue;
                }
                var param = method.GetParameters();
                if (param.Length == 0)
                {
                    Debug.LogError($"MessageHandler {method.DeclaringType.Name}.{method.Name} 消息处理器至少有一个参数！");
                    continue;
                }
                var attr = method.GetCustomAttribute<MessageHandlerAttribute>();
                switch (attr.type)
                {
                    case MessageType.Normal:
                        if (param.Length != 2
                            || (param.Length == 2 && param[0].ParameterType != typeof(Session) && !typeof(Message).IsAssignableFrom(param[1].ParameterType)))
                        {
                            Debug.LogError($"常规消息处理器 {method.Name} 必须有2个参数, 左侧 Session，右侧 Message!");
                            continue;
                        }
                        var msgType = param[1].ParameterType;
                        if (!normalHandlers.TryGetValue(msgType, out var info))
                        {
                            info = new NormalHandlerInfo
                            {
                                method = method,
                                Message = msgType
                            };
                            normalHandlers.Add(msgType, info);
                        }
                        else
                        {
                            Debug.LogError($"{nameof(MessageManager)}: 请不要重复注册 {msgType.Name} 处理器，此消息已被 {info.method.DeclaringType.Name}.{info.method.Name}中处理 ");
                        }
                        break;
                    case MessageType.RPC:
                        if (param.Length != 3
                        || (param.Length == 3 && param[0].ParameterType != typeof(Session)
                        && !typeof(Request).IsAssignableFrom(param[1].ParameterType)
                        && !typeof(Response).IsAssignableFrom(param[2].ParameterType)))
                        {
                            Debug.LogError($"RPC消息处理器 {method.Name} 必须有3个参数, 左侧 Session，中间 Request，右侧 Response!");
                            continue;
                        }
                        var reqType = param[1].ParameterType;
                        var rspType = param[2].ParameterType;
                        if (!rpcHandlers.TryGetValue(reqType, out var rpcInfo))
                        {
                            rpcInfo = new RpcHandlerInfo
                            {
                                method = method,
                                Request = reqType,
                                Response = rspType
                            };
                            rpcHandlers.Add(reqType, rpcInfo);
                        }
                        else
                        {
                            Debug.LogError($"{nameof(MessageManager)}: 请不要重复注册 {reqType.Name} 处理器，此消息已被 {rpcInfo.method.DeclaringType.Name}.{rpcInfo.method.Name}中处理 ");
                        }
                        break;
                    default:
                        break;
                }
            }
        }

        internal static void HandleNormalMessage(Session session, IMessage message)
        {
            if (normalHandlers.TryGetValue(message.GetType(), out var info))
            {
                info.method.Invoke(null, new object[] { session, message });
            }
        }

        internal static async void HandleRpcRequest(Session session, IRequest request)
        {
            if (rpcHandlers.TryGetValue(request.GetType(), out var info))
            {
                if (rpcMessagePairs.TryGetValue(info.Request, out var responseType))
                {
                    var response = Activator.CreateInstance(responseType) as IResponse;
                    response.Id = request.Id;
                    var task = info.method.Invoke(null, new object[] { session, request, response });
                    await (task as Task);
                    session.Reply(response);
                    return;
                }
            }
            var response_fallback = new Response
            {
                Id = request.Id,
                Error = $"RPC 消息 {request.GetType().Name} 没有找到对应的处理器！"
            };
            session.Reply(response_fallback);
        }
        internal static void HandleRpcResponse(Session session, IResponse response)
        {
            if (rpcInfoPairs.TryGetValue(response.Id, out var rpcInfo))
            {
                rpcInfo.task.SetResult(response);
                rpcInfoPairs.Remove(response.Id);
            }
        }

        internal static Task<IResponse> AddRpcTask(IRequest request)
        {
            var tcs = new TaskCompletionSource<IResponse>();
            var cts = new CancellationTokenSource();
            var timeout = Mathf.Max(request.Timeout, 5000); //至少等待 5 秒的响应机会，这在发生复杂操作时很有效
            cts.CancelAfter(timeout);
            var exception = new TimeoutException($"RPC Call Timeout! Request: {request}");
            cts.Token.Register(() => tcs.TrySetException(exception), useSynchronizationContext: false);
            var rpcinfo = new RpcInfo
            {
                id = request.Id,
                task = tcs,
            };
            rpcInfoPairs.Add(request.Id, rpcinfo);
            return tcs.Task;
        }

        // 获取消息对应的 Response 类型
        public static Type GetResponseType([NotNull] IRequest request)
        {
            if (!rpcMessagePairs.TryGetValue(request.GetType(), out var type))
            {
                throw new Exception($"RPC 消息  Request-Response 为正确完成映射，请参考示例正确注册映射关系！");
            }
            return type;
        }

        //todo: Normal Message 与 RPC Message 的处理器注册机制不一样，需要分开处理
        // Normal Message 的处理器注册机制：
        // 使用 AddSignal<T>(OnXXXXMessageReceived) 添加消息处理器，T 为消息类型
        // 使用 RemoveSignal<T>(OnXXXXMessageReceived) 移除消息处理器，T 为消息类型
        // Normal 消息，必须有2个参数，第一个参数为 Session，第二个参数为 Message 
        // 示例如下：
        /*
         MessageManager.AddSignal<TestClass>(OnTestClassMessageReceived);
         private static void OnTestClassMessageReceived(Session session, TestClass message)
         {
             Debug.Log($"{nameof(MessageManager)}: 收到 {session} message {message}");
         }
         */

        //todo ：
        // 加入 message id 机制, 用于快速获取消息Type,方便调试
        // id 使用二进制偏移表示，这样方便 与、或 运算判断消息是否存在于 logfilter，作为消息的身份id也很合理
        // .proto 就参考 et6.0 的 protobuf 语法，自己解析，支持多 proto文件，支持输出多脚本（partial）
        // 看实际情况，可能不需要代码分析器了，
        // 关于 IL 代码注入：
        // 1. 取消 MessageHandlerAttribute 的使用，改为在静态函数中自动注册
        // 2. 如果用户删除了 MessageHandlerAttribute，自动删除静态构造函数内的注册逻辑，如果因此静态函数body空了就删除静态函数
        //
        // 关于撰写 Server 端 handler是否符合规范：
        // 0. 如果使用 MessageHandler 标记，不管是 Normal 消息还是 RPC 消息，必须是静态方法且 RPC 消息返回值必须是 Task
        // 1. 如果是 RPC 消息，必须有3个参数，第一个参数为 Session，第二个参数为 Request，第三个参数为 Response
        // 2. 如果是 Normal 消息，必须有2个参数，第一个参数为 Session，第二个参数为 Message
        // 3. RPC Hanlder 理应在客户端上也能实现，进而实现 Server call Client （已经验证）
        // 4. RPC Handler 的 Request 需要上报 RPC Server（可能是客户端）没有实现 Handler 的情况（已经实现）
        // 5. 支持 MessageHandler 标记消息处理器，也支持 AddHandler 、RemoveHandler 让用户自发添加消息处理器（观察者模式）
        // 6. RPC 、Normal 都支持用户自发的 AddHandler、RemoveHandler
        // 7. 使用委托注册而不是反射来处理 handler 的注册

        class NormalHandlerInfo : BaseHandlerInfo
        {
            public Type Message;
        }
        class BaseHandlerInfo
        {
            public MethodInfo method;
        }
        class RpcHandlerInfo : BaseHandlerInfo
        {
            public Type Request;
            public Type Response;
        }
    }
}
