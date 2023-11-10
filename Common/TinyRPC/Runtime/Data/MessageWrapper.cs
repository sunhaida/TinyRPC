using System;
using System.Reflection;
using UnityEngine;
namespace zFramework.TinyRPC.Messages
{
    // 由于jsonutility无法序列化继承关系，所以需要一个包装类
    [Serializable]
    public class MessageWrapper : ISerializationCallbackReceiver
    {
        public string type;
        public string data;
        public IMessage Message { get; set; }
        public void OnBeforeSerialize()
        {
            type = Message.GetType().FullName;
            data = JsonUtility.ToJson(Message);
        }

        public void OnAfterDeserialize()
        {
            var asmname = type switch
            {
                "zFramework.TinyRPC.Ping" => "com.zframework.tinyrpc.runtime", // ping asm
                "zFramework.TinyRPC.Messages.Response" => "com.zframework.tinyrpc.runtime", // fallback response asm
                _ => "com.zframework.tinyrpc.generated"
            };
            var asm = Assembly.Load(asmname);
            Type t = asm.GetType(type);
            Message = (IMessage)JsonUtility.FromJson(data, t);
        }

        public override string ToString() 
        {
            var info = nameof(MessageWrapper);
            var fields = GetType().GetFields();
            foreach (var field in fields)
            {
                info += $"{field.Name} = {field.GetValue(this)}\n";
            }
            return info;
        }
    }
}
