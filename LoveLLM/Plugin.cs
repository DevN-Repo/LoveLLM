using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LoveLLM;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using System.Reflection.Emit;
using BepInEx.Configuration;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Reflection;
using System.IO;

namespace repo_mod;

public static class Native
{

    [DllImport("LLMNative.dll")]
    public static extern IntPtr GenerateToken(string str, IntPtr model, int n_ctx);

    [DllImport("LLMNative.dll")]
    public static extern bool PollToken(IntPtr req);

    [DllImport("LLMNative.dll")]
    public static extern IntPtr RetrieveToken(IntPtr req);


    [DllImport("LLMNative.dll")]
    public static extern void FreeToken(IntPtr req);

    [DllImport("LLMNative.dll")]
    public static extern IntPtr LoadModel(string path, int ngl);

    [DllImport("LLMNative.dll")]
    public static extern void FreeModel(IntPtr model);
}

static internal class PatchUtils
{
    public struct OpcodeMatch
    {
        public OpCode opcode;
        public object operandOrNull = null;

        public OpcodeMatch(OpCode opcode)
        {
            this.opcode = opcode;
        }
        public OpcodeMatch(OpCode opcode, object operand)
        {
            this.opcode = opcode;
            this.operandOrNull = operand;
        }
    }

    public static int LocateCodeSegment(int startIndex, List<CodeInstruction> searchSpace, List<OpcodeMatch> searchFor)
    {
        if (startIndex < 0 || startIndex >= searchSpace.Count) return -1;

        int searchForIndex = 0;
        for (int searchSpaceIndex = startIndex; searchSpaceIndex < searchSpace.Count; searchSpaceIndex++)
        {
            CodeInstruction check = searchSpace[searchSpaceIndex];
            OpcodeMatch currentMatch = searchFor[searchForIndex];
            if (check.opcode == currentMatch.opcode)
            {
                searchForIndex++;
                if (searchForIndex == searchFor.Count)
                {
                    //we found the sequence, return the index at the start of the sequence
                    return searchSpaceIndex - searchForIndex + 1;
                }
            }
            else
            {
                searchForIndex = 0;
            }
        }

        //we got to the end and didnt find the sequence
        return -1;
    }
}


internal class PromptConfig
{
    public struct Prompt
    {
        public string PromptString { get; set; }
        public ChatManager.PossessChatID PossessChatID { get; set; }
    }


    public static List<Prompt> Prompts { get; set; } 
}


[HarmonyPatch(typeof(ValuableLovePotion))]
internal class ValuableLovePotionPatches
{
    public static IntPtr? LoadedModel = null;
    public static ConfigEntry<int> NCtxConfig;

    public class InternalStateData
    {
        public enum EState
        {
            State_Idle,
            State_Cooldown,
            State_WaitingForToken,
            State_TokenAvailable
        }

        public PromptConfig.Prompt? currentPrompt = null;
        public IntPtr? currentRequest = null;
        public EState state = EState.State_Cooldown;
        public float coolDownUntilNextToken = 3.0f;
    }

    internal static void SelectPrompt(InternalStateData stateData)
    {
        int randomIndex = UnityEngine.Random.RandomRangeInt(0, PromptConfig.Prompts.Count);
        stateData.currentPrompt = PromptConfig.Prompts[randomIndex];
    }

    internal static string GetPrompt(string inPlayerName, PromptConfig.Prompt prompt)
    {
        var playerName = inPlayerName;
        if (playerName.Equals("this potion") || playerName.Equals("[playerName]"))
            playerName = "Doug"; //because everyone hates doug.

        return prompt.PromptString.Replace("{playerName}", $"\"{playerName}\"");
    }

    internal static string GetCurrentPlayerName(ValuableLovePotion instance)
    {
        FieldInfo playerNameField = AccessTools.Field(typeof(ValuableLovePotion), "playerName");
        return (string)playerNameField.GetValue(instance);
    }

    public static Dictionary<ValuableLovePotion, InternalStateData> internalStateStore = new();

    [HarmonyPatch("Update")]
    [HarmonyPrefix]
    private static void Update_Prefix(ValuableLovePotion __instance)
    {
        if (!internalStateStore.ContainsKey(__instance))
        {
            InternalStateData newStateData = new()
            {
                state = InternalStateData.EState.State_Cooldown,
                coolDownUntilNextToken = 15.0f
            };

            internalStateStore.Add(__instance, newStateData);
        }

        if (internalStateStore.TryGetValue(__instance, out InternalStateData controllerData))
        {
            switch (controllerData.state)
            {
                case InternalStateData.EState.State_Idle:
                    SelectPrompt(controllerData);
                    string prompt = GetPrompt(GetCurrentPlayerName(__instance), controllerData.currentPrompt.Value);

                    Plugin.Logger.LogInfo($"Requesting prompt: {prompt}");
                    controllerData.currentRequest = Native.GenerateToken(prompt, LoadedModel.Value, NCtxConfig.Value);
                    controllerData.state = InternalStateData.EState.State_WaitingForToken;
                    goto case InternalStateData.EState.State_WaitingForToken;
                case InternalStateData.EState.State_WaitingForToken:
                    bool tokenReady = Native.PollToken(controllerData.currentRequest.Value);
                    if (tokenReady)
                    {
                        controllerData.state = InternalStateData.EState.State_TokenAvailable;
                        goto case InternalStateData.EState.State_TokenAvailable;
                    }
                    break;
                case InternalStateData.EState.State_TokenAvailable:
                    break;
                case InternalStateData.EState.State_Cooldown:
                    controllerData.coolDownUntilNextToken -= Time.deltaTime;
                    if (controllerData.coolDownUntilNextToken <= 0)
                    {
                        controllerData.coolDownUntilNextToken = 0.0f;
                        controllerData.state = InternalStateData.EState.State_Idle;
                    }
                    break;
            }
        }
    }
    
    
    static void Hooked_UpdateStateMachine(ValuableLovePotion __instance)
    {
        if(internalStateStore.TryGetValue(__instance, out InternalStateData internalState))
        {
            if(internalState.state == InternalStateData.EState.State_TokenAvailable)
            {
                var ptr = Native.RetrieveToken(internalState.currentRequest.Value);
                string result = Marshal.PtrToStringAnsi(ptr);

                ChatManager.PossessChatID vibe = internalState.currentPrompt.Value.PossessChatID;
                Color _possessColor = new Color(1f, 1f, 1f, 1f);
                switch (vibe)
                {
                    case ChatManager.PossessChatID.LovePotion:
                        _possessColor = new Color(1f, 0.3f, 0.6f, 1f);
                        break;
                    case ChatManager.PossessChatID.Betrayal:
                        _possessColor = new Color(0.5f, 0.01f, 0.01f);
                        break;
                    default:
                        break;

                }
                ChatManager.instance.PossessChatScheduleStart(10);
                Plugin.Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} Got token: {result}");
                ChatManager.instance.PossessChat(internalState.currentPrompt.Value.PossessChatID, result, 1.0f, _possessColor);
                ChatManager.instance.PossessChatScheduleEnd();
                internalState.coolDownUntilNextToken = UnityEngine.Random.Range(25f, 30f);
                internalState.state = InternalStateData.EState.State_Cooldown;
                Native.FreeToken(internalState.currentRequest.Value);
            }
        }
    }

    [HarmonyPatch("StateIdle")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> StateIdle_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var codeSegment = new List<PatchUtils.OpcodeMatch>([
            new (OpCodes.Ldc_R4, 1.0),
            new (OpCodes.Ldc_R4, 0.3),
            new (OpCodes.Ldc_R4, 0.6),
            new (OpCodes.Ldc_R4, 1.0)
            ]);
        int searchTargetIndex = PatchUtils.LocateCodeSegment(0, codes, codeSegment);

        if (searchTargetIndex == -1)
        {
            Plugin.Logger.LogError("Could not transpile ValuableLovePotion.Update");
            return instructions;
        }

        //int start = searchTargetIndex - 7;
        int i = searchTargetIndex - 7;
        while (true)
        {
            if (codes[i].opcode == OpCodes.Ret)
            {
                break;
            }
            codes[i] = new CodeInstruction(OpCodes.Nop);

            i++;
        }

        codes[searchTargetIndex - 7] = new CodeInstruction(OpCodes.Ldarg_0); //Load this.
        codes[searchTargetIndex - 6] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ValuableLovePotionPatches), nameof(Hooked_UpdateStateMachine)));

        //foreach (var code in codes)
        //{
        //    Plugin.Logger.LogInfo($"{code.opcode} {code.operand}");
        //}

        /*
    IL_0149: ldloc.0      // flag
    IL_014a: brfalse.s    IL_01a8

    // [102 7 - 102 72]
    IL_014c: ldarg.0      // this
    IL_014d: call         instance string ValuableLovePotion::GenerateAffectionateSentence()
    IL_0152: stloc.s      affectionateSentence

         IL_0154: ldarg.0      // this
    IL_0155: ldc.i4.1
    IL_0156: stfld        valuetype ValuableLovePotion/State ValuableLovePotion::currentState

    // [104 7 - 104 58]
    IL_015b: ldloca.s     _possessColor
    IL_015d: ldc.r4       1
    IL_0162: ldc.r4       0.3
    IL_0167: ldc.r4       0.6
    IL_016c: ldc.r4       1
    IL_0171: call         instance void [UnityEngine.CoreModule]UnityEngine.Color::.ctor(float32, float32, float32, float32)

    // [105 7 - 105 56]
    IL_0176: ldsfld       class ChatManager ChatManager::'instance'
    IL_017b: ldc.i4.s     10 // 0x0a
    IL_017d: callvirt     instance void ChatManager::PossessChatScheduleStart(int32)

    // [106 7 - 106 118]
    IL_0182: ldsfld       class ChatManager ChatManager::'instance'
    IL_0187: ldc.i4.1
    IL_0188: ldloc.s      affectionateSentence
    IL_018a: ldc.r4       1
    IL_018f: ldloc.s      _possessColor
    IL_0191: ldc.r4       0.0
    IL_0196: ldc.i4.0
    IL_0197: ldc.i4.0
    IL_0198: ldnull
    IL_0199: callvirt     instance void ChatManager::PossessChat(valuetype ChatManager/PossessChatID, string, float32, valuetype [UnityEngine.CoreModule]UnityEngine.Color, float32, bool, int32, class [UnityEngine.CoreModule]UnityEngine.Events.UnityEvent)

    // [107 7 - 107 52]
    IL_019e: ldsfld       class ChatManager ChatManager::'instance'
    IL_01a3: callvirt     instance void ChatManager::PossessChatScheduleEnd()
*/

        return codes;
    }
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    internal static Harmony? Harmony { get; set; }
    public static Plugin Instance { get; private set; } = null!;

    internal static ConfigEntry<string> ModelPathConfig;
    internal static ConfigEntry<string> PromptFileConfig;
    internal static ConfigEntry<int> NglConfig;

    private void InitModel()
    {
        const int defaultNgl = 99;
        NglConfig = Config.Bind("General",
             "ngl",
             defaultNgl,
             "Ngl parameter to pass to llama");

        const int defaultNCtx = 2048;
        ValuableLovePotionPatches.NCtxConfig = Config.Bind("General",
             "n_ctx",
             defaultNCtx,
             "n_ctx parameter to pass to llama. Increasing this will increase the memory footprint and maybe get some better responses. Idk its finnciky..");

        string defaultPath = "Models/SmolLM2-1.7B.gguf";
        ModelPathConfig = Config.Bind("General",      // The section under which the option is shown
                     "Model",  // The key of the configuration option in the configuration file
                     defaultPath, // The default value
                     "Model to load");
        string modPath = Path.GetDirectoryName(Info.Location);
        var modelPath = Path.Combine(modPath, ModelPathConfig.Value);
        Logger.LogInfo($"INIT MODEL {modelPath}");

        ValuableLovePotionPatches.LoadedModel = Native.LoadModel(modelPath, NglConfig.Value);
    }

    private void InitPrompts()
    {
        string defaultPath = "Models/Prompts.txt";
        PromptFileConfig = Config.Bind("General",      // The section under which the option is shown
                     "Prompts",  // The key of the configuration option in the configuration file
                     defaultPath, // The default value
                     "Prompt config. Each entry in the prompt config is delimited by a new line, and separated with a semicolon. The value to the left of the semicolon is the prompt and to the right is the chat possess id, so 1 is pink love potion and 4 is red.");
        string modPath = Path.GetDirectoryName(Info.Location);
        var promptFilePath = Path.Combine(modPath, PromptFileConfig.Value);
        Logger.LogInfo($"INIT PROMPTS {promptFilePath}");
        string content = File.ReadAllText(promptFilePath);
        string[] entries = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        List<PromptConfig.Prompt> result = new List<PromptConfig.Prompt>();

        // Process each entry
        foreach (var entry in entries)
        {
            string[] token = entry.Split(";");
            PromptConfig.Prompt prompt = new PromptConfig.Prompt(); 
            prompt.PromptString = token[0];
            prompt.PossessChatID = (ChatManager.PossessChatID)Enum.Parse(typeof(ChatManager.PossessChatID), token[1]);
            result.Add(prompt);

            Logger.LogInfo($"Loaded prompt {prompt.PromptString} -- {prompt.PossessChatID}");
        }

        PromptConfig.Prompts = result;
    }

    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;
        Instance = this;

        InitPrompts();
        InitModel();
        Patch();

        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} has loaded!");
    }

    public void OnApplicationQuit()
    {
        // This will be executed when the game shuts down
        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} is unloading... Game is shutting down or plugin is disabled.");
        Native.FreeModel(ValuableLovePotionPatches.LoadedModel.Value);
    }

    internal static void Patch()
    {
        Harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);

        Logger.LogDebug("Patching...");

        Harmony.PatchAll();

        Logger.LogDebug("Finished patching!");
    }

    internal static void Unpatch()
    {
        Logger.LogDebug("Unpatching...");

        Harmony?.UnpatchSelf();

        Logger.LogDebug("Finished unpatching!");
    }
}
