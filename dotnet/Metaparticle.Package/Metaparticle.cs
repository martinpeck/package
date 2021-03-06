﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
//using Mono.Unix;
using dockerfile;
using System.Text;
using static Metaparticle.Package.Util;

namespace Metaparticle.Package
{
    public class Driver
    {
        private Config config;
        private Metaparticle.Runtime.Config runtimeConfig;

        public Driver(Config config, Metaparticle.Runtime.Config runtimeConfig) {
            this.config = config;
            this.runtimeConfig = runtimeConfig;
        }

        private ImageBuilder getBuilder() {
            switch (config.Builder) {
                case "docker":
                    return new DockerBuilder();
                case "aci":
                    return new DockerBuilder();
                default:
                    return null;
            }
        }

        private ContainerExecutor getExecutor() {
            if (runtimeConfig == null) {
                return null;
            }
            switch (runtimeConfig.Executor) {
                case "docker":
                    return new DockerExecutor();
                case "aci":
                    return new AciExecutor();
                case "metaparticle":
                    return new MetaparticleExecutor();
                default:
                    return null;
            }
        }

        private static string getArgs(string[] args) {
            if (args == null || args.Length == 0) {
                return "";
            }
            var b = new StringBuilder();
            foreach (var arg in args) {
                b.Append(arg);
                b.Append(" ");
            }
            return b.ToString().Trim();
        }

        public void Build(string[] args)
        {
            var proc = Process.GetCurrentProcess();
            var procName = proc.ProcessName;
            string exe = null;
            string dir = null;
            TextWriter o = config.Verbose ? Console.Out : null;
            TextWriter e = config.Quiet ? Console.Error : null;
            if (procName == "dotnet")
            {
                dir = "bin/release/netcoreapp2.0/debian.8-x64/publish";
                Exec("dotnet", "publish -r debian.8-x64 -c release", stdout: o, stderr: e);
                //var dirInfo = new UnixDirectoryInfo(dir);
                var files = Directory.GetFiles(dir);
                foreach (var filePath in files)
                {
                    var file = new FileInfo(filePath);
                    if (file.Name.EndsWith(".runtimeconfig.json"))
                    {
                        exe = file.Name.Substring(0, file.Name.Length - ".runtimeconfig.json".Length);
                    }
                }
            }
            else
            {
                exe = procName;
                var prog = proc.MainModule.FileName;
                dir = Directory.GetParent(prog).FullName;
            }
            var instructions = new List<Instruction>();
            instructions.Add(new Instruction("FROM", "debian:9"));
            instructions.Add(new Instruction("RUN", " apt-get update && apt-get -qq -y install libunwind8 libicu57 libssl1.0 liblttng-ust0 libcurl3 libuuid1 libkrb5-3 zlib1g"));
            // TODO: lots of things here are constant, figure out how to cache for perf?
            instructions.Add(new Instruction("COPY", string.Format("* /exe/", dir)));
            instructions.Add(new Instruction("CMD", string.Format("/exe/{0} {1}", exe, getArgs(args))));

            var df = new Dockerfile(instructions.ToArray(), new Comment[0]);
            var dockerfilename = dir + "/Dockerfile";
            File.WriteAllText(dockerfilename, df.Contents());

            var builder = getBuilder();

            string imgName = (string.IsNullOrEmpty(config.Repository) ? exe : config.Repository);
            if (!string.IsNullOrEmpty(config.Version)) {
                imgName += ":" + config.Version;
            }
            if (!builder.Build(dockerfilename, imgName, stdout: o, stderr: e)) {
                Console.Error.WriteLine("Image build failed.");
                return;
            }

            if (config.Publish) {
                if (!builder.Push(imgName, stdout: o, stderr: e)) {
                    Console.Error.WriteLine("Image push failed.");
                    return;
                }
            }

            if (runtimeConfig == null) {
                return;
            }

            var exec = getExecutor();
            var id = exec.Run(imgName, runtimeConfig);

            Console.CancelKeyPress += delegate {
                exec.Cancel(id);
            };

            exec.Logs(id, Console.Out, Console.Error);
        }

        public static bool InDockerContainer()
        {
            var inContainer = System.Environment.GetEnvironmentVariable("METAPARTICLE_IN_CONTAINER");
            if ("true".Equals(inContainer)) {
                return true;
            }
            // This only works on Linux
            const string cgroupPath = "/proc/1/cgroup";
            if (File.Exists(cgroupPath)) {
                var info = File.ReadAllText(cgroupPath);
                // This is a little approximate...
                return info.IndexOf("docker") != -1 || info.IndexOf("kubepods") != -1;
            }
            return false;
        }

        public static void Containerize(string[] args, Action main)
        {
            if (InDockerContainer())
            {
                main();
                return;
            }
            Config config = new Config();
            Metaparticle.Runtime.Config runtimeConfig = null;
            var trace = new StackTrace();
            foreach (object attribute in trace.GetFrame(1).GetMethod().GetCustomAttributes(true))
            {
                if (attribute is Config)
                {
                    config = (Config) attribute;
                }
                if (attribute is Metaparticle.Runtime.Config) {
                    runtimeConfig = (Metaparticle.Runtime.Config) attribute;
                }
            }
            var mp = new Driver(config, runtimeConfig);
            mp.Build(args);
        }
    }
}
