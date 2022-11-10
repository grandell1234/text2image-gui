﻿using StableDiffusionGui.Data;
using StableDiffusionGui.Io;
using StableDiffusionGui.Main;
using StableDiffusionGui.MiscUtils;
using StableDiffusionGui.Os;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StableDiffusionGui.Implementations
{
    internal class SdOnnx
    {
        public static async Task Run(string[] prompts, string negPrompt, int iterations, Dictionary<string, string> parameters, string outPath)
        {
            try
            {
                string[] initImgs = parameters.FromJson<string[]>("initImgs");
                string embedding = parameters.FromJson<string>("embedding");
                float[] initStrengths = parameters.FromJson<float[]>("initStrengths");
                int steps = parameters.FromJson<int>("steps");
                float[] scales = parameters.FromJson<float[]>("scales");
                long seed = parameters.FromJson<long>("seed");
                string sampler = parameters.FromJson<string>("sampler");
                Size res = parameters.FromJson<Size>("res");
                var seamless = parameters.FromJson<Enums.StableDiffusion.SeamlessMode>("seamless");
                string model = parameters.FromJson<string>("model");
                bool lockSeed = parameters.FromJson<bool>("lockSeed");

                var cachedModels = Paths.GetModels(Enums.StableDiffusion.ModelType.Normal, Enums.StableDiffusion.Implementation.DiffusersOnnx);
                Model modelDir = TtiUtils.CheckIfCurrentSdModelExists();

                if (modelDir == null)
                    return;

                Dictionary<string, string> initImages = initImgs != null && initImgs.Length > 0 ? await TtiUtils.CreateResizedInitImagesIfNeeded(initImgs.ToList(), res) : null;

                long startSeed = seed;

                List<Dictionary<string, string>> argLists = new List<Dictionary<string, string>>(); // List of all args for each command
                Dictionary<string, string> args = new Dictionary<string, string>(); // List of args for current command
                args["prompt"] = "";
                args["default"] = "";

                foreach (string prompt in prompts)
                {
                    List<string> processedPrompts = PromptWildcardUtils.ApplyWildcardsAll(prompt, iterations, false);
                    TextToImage.CurrentTaskSettings.ProcessedAndRawPrompts = processedPrompts.Distinct().ToDictionary(x => x, x => prompt);

                    for (int i = 0; i < iterations; i++)
                    {
                        args.Remove("initImg");
                        args.Remove("initStrength");
                        args["prompt"] = processedPrompts[i];
                        args["negprompt"] = negPrompt;
                        args["steps"] = $"{steps}";
                        args["w"] = $"{res.Width}";
                        args["h"] = $"{res.Height}";
                        args["seed"] = $"{seed}";

                        foreach (float scale in scales)
                        {
                            args["scale"] = $"{scale.ToStringDot()}";

                            if (initImages == null) // No init image(s)
                            {
                                argLists.Add(new Dictionary<string, string>(args));
                            }
                            else // With init image(s)
                            {
                                foreach (string initImg in initImages.Values)
                                {
                                    foreach (float strength in initStrengths)
                                    {
                                        args["initImg"] = $"--init_img {initImg.Wrap()}";
                                        args["initStrength"] = $"--strength {strength.ToStringDot("0.###")}";
                                        argLists.Add(new Dictionary<string, string>(args));
                                    }
                                }
                            }
                        }

                        if (!lockSeed)
                            seed++;
                    }

                    if (Config.GetBool(Config.Key.checkboxMultiPromptsSameSeed))
                        seed = startSeed;
                }

                string jsonPath = Path.Combine(Paths.GetSessionDataPath(), "prompts-onnx.json");
                File.WriteAllText(jsonPath, argLists.ToJson(Newtonsoft.Json.Formatting.Indented));

                Logger.Log($"Running Stable Diffusion - {iterations} Iterations, {steps} Steps, Scales {(scales.Length < 4 ? string.Join(", ", scales.Select(x => x.ToStringDot())) : $"{scales.First()}->{scales.Last()}")}, {res.Width}x{res.Height}, Starting Seed: {startSeed}");

                //string argsStartup = Args.InvokeAi.GetArgsStartup(embedding);

                string initsStr = initImages != null ? $" and {initImages.Count} image{(initImages.Count != 1 ? "s" : "")} using {initStrengths.Length} strength{(initStrengths.Length != 1 ? "s" : "")}" : "";
                Logger.Log($"{prompts.Length} prompt{(prompts.Length != 1 ? "s" : "")} with {iterations} iteration{(iterations != 1 ? "s" : "")} each and {scales.Length} scale{(scales.Length != 1 ? "s" : "")}{initsStr} each = {argLists.Count} images total.");

                Process py = OsUtils.NewProcess(!OsUtils.ShowHiddenCmd());
                TextToImage.CurrentTask.Processes.Add(py);

                py.StartInfo.RedirectStandardInput = true;
                py.StartInfo.Arguments = $"{OsUtils.GetCmdArg()} cd /D {Paths.GetDataPath().Wrap()} && {TtiUtils.GetEnvVarsSd()} && call activate.bat {Constants.Dirs.SdEnv} && " +
                    $"python \"{Constants.Dirs.RepoSd}/sd_onnx/sd_onnx.py\" -m {modelDir.FullName.Wrap(true)} -j {jsonPath.Wrap(true)} -o {outPath.Wrap(true)}";

                Logger.Log("cmd.exe " + py.StartInfo.Arguments, true);

                if (!OsUtils.ShowHiddenCmd())
                {
                    py.OutputDataReceived += (sender, line) => { TtiProcessOutputHandler.LogOutput(line.Data); };
                    py.ErrorDataReceived += (sender, line) => { TtiProcessOutputHandler.LogOutput(line.Data, true); };
                }

                if (TtiProcess.CurrentProcess != null)
                {
                    TtiProcess.ProcessExistWasIntentional = true;
                    OsUtils.KillProcessTree(TtiProcess.CurrentProcess.Id);
                }

                TtiProcessOutputHandler.Start();

                Logger.Log($"Loading Stable Diffusion (ONNX) with model {Path.ChangeExtension(model, null).Trunc(80).Wrap()}...");

                TtiProcess.ProcessExistWasIntentional = false;
                py.Start();
                TtiProcess.CurrentProcess = py;
                OsUtils.AttachOrphanHitman(py);
                    
                if (!OsUtils.ShowHiddenCmd())
                {
                    py.BeginOutputReadLine();
                    py.BeginErrorReadLine();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Unhandled Stable Diffusion Error: {ex.Message}");
                Logger.Log(ex.StackTrace, true);
            }
        }
    }
}