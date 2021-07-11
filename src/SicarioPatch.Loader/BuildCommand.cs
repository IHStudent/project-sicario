﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SicarioPatch.Core;
using SicarioPatch.Integration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SicarioPatch.Loader
{
    public class BuildCommand : AsyncCommand<BuildCommand.Settings>
    {
        private readonly IAnsiConsole _console;
        private readonly SkinSlotLoader _slotLoader;
        private readonly IMediator _mediator;
        private readonly PresetFileLoader _presetLoader;
        private readonly MergeLoader _mergeLoader;
        private readonly IConfiguration _config;
        private readonly Integration.GameFinder _gameFinder;
        private readonly ILogger<BuildCommand> _logger;
#pragma warning disable 8618
        public class Settings : CommandSettings
        {
            [CommandOption(("-r|--run"))]

            public FlagValue<bool> RunAfterBuild { get; set; }

            [CommandOption("--installPath")]
            public string? InstallPath { get; set; }
            
            [CommandArgument(0, "[presetPaths]")]
            public string[]? PresetPaths { get; init; }
            
            [CommandOption("--no-clean")]
            public FlagValue<bool> SkipTargetClean { get; init; }
        }
#pragma warning restore 8618

        public BuildCommand(IAnsiConsole console, SkinSlotLoader slotLoader, IMediator mediator,
            PresetFileLoader presetLoader, MergeLoader mergeLoader, IConfiguration config,
            Integration.GameFinder gameFinder, ILogger<BuildCommand> logger) {
            _console = console;
            _slotLoader = slotLoader;
            _mediator = mediator;
            _presetLoader = presetLoader;
            _mergeLoader = mergeLoader;
            _config = config;
            _gameFinder = gameFinder;
            _logger = logger;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings) {
            if (string.IsNullOrWhiteSpace(settings.InstallPath)) {
                var install = _gameFinder.GetGamePath();
                if (install == null) {
                    _console.MarkupLine("[bold red]Error![/] [orange]Could not locate game install folder![/]");
                    return 412;
                }
                settings.InstallPath = install;
            }
            
            if (!Directory.Exists(settings.InstallPath)) {
                _console.MarkupLine($"[red][bold]Install not found![/] The game install directory doesn't exist.[/]");
                return 404;
            }

            var paksRoot = Path.Join(settings.InstallPath, "ProjectWingman", "Content", "Paks");

            _config["GamePath"] = settings.InstallPath;
            _config["GamePakPath"] = Path.Join(paksRoot, "ProjectWingman-WindowsNoEditor.pak");

            var presetSearchPaths = new List<string>(settings.PresetPaths ?? Array.Empty<string>()) {
                Path.Join(settings.InstallPath, "ProjectWingman", "Content", "Presets"),
                Path.Join(paksRoot, "~mods")
            };
            
            _logger.LogDebug($"Searching {presetSearchPaths.Count} paths for loose presets");
            
            var existingMods = _mergeLoader.GetSicarioMods().ToList();
            var mergedInputs = existingMods
                .Select(m => m.TemplateInputs)
                .Aggregate(new Dictionary<string, string>(), (total, next) => next.MergeLeft(total));
            
            _console.MarkupLine($"[dodgerblue2]Loaded [bold]{existingMods.Count}[/] Sicario mods for rebuild[/]");

            var embeddedPresets = _mergeLoader.LoadPresetsFromMods().ToList();
            var embeddedInputs = embeddedPresets
                .Select(m => m.ModParameters)
                .Aggregate(mergedInputs,
                    (total, next) => total.MergeLeft(next)
                );
            
            _console.MarkupLine($"[dodgerblue2]Loaded [bold]{embeddedPresets.Count}[/] embedded presets from installed mods[/]");
            
            var presetPaths = presetSearchPaths
                    .Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, "*.dtp", SearchOption.AllDirectories));
            var presets = _presetLoader.LoadFromFiles(presetPaths).ToList();
            
            _console.MarkupLine($"[dodgerblue2]Loaded [bold]{presets.Count}[/] loose presets from file.[/]");

            var mergedPresetInputs = presets
                .Select(p => p.ModParameters)
                .Aggregate(embeddedInputs,
                (total, next) => total.MergeLeft(next)
                );

            _console.MarkupLine($"Final mod will be built with [dodgerblue2]{mergedPresetInputs.Keys.Count}[/] parameters");


            var slotLoader = _slotLoader.GetSlotMod();
            
            _console.MarkupLine($"[dodgerblue2]Successfully compiled skin merge with [bold]{slotLoader.GetPatchCount()}[/] patches.[/]");

            var allMods = existingMods.SelectMany(m => m.Mods).ToList();
            allMods.AddRange(embeddedPresets.SelectMany(p => p.Mods));
            allMods.AddRange(presets.SelectMany(p => p.Mods));
            allMods.Add(slotLoader);
            
            _console.MarkupLine($"[bold darkblue]Queuing mod build with {allMods.Count} mods[/]");

            var req = new PatchRequest(allMods) {
                PackResult = true,
                TemplateInputs = mergedPresetInputs,
                Name = "SicarioMerge",
                UserName = $"loader:{Environment.MachineName}"
            };
            var resp = await _mediator.Send(req);
            _console.MarkupLine($"[green][bold]Success![/] Your merged mod has been built and is now being installed to the game folder[/]");
            var targetPath = Path.Join(paksRoot, "~sicario");
            if (!Directory.Exists(targetPath)) {
                Directory.CreateDirectory(targetPath);
            }

            if (Directory.GetFiles(targetPath).Any() && !(settings.SkipTargetClean.IsSet && settings.SkipTargetClean.Value)) {
                foreach (var file in Directory.GetFiles(targetPath)) {
                    File.Delete(file);
                }
            }
            resp.MoveTo(Path.Join(targetPath, resp.Name));
            _console.MarkupLine($"[dodgerblue2]Your merged mod is installed and you can start the game.[/]");
            return 0;
        }
    }
}