// w14/ui-settings (#296, #312, #302): the Settings page's strings, in their own table so lanes adding to UiStrings do not
// collide with it. Same row shape as UiStrings: key, PT-BR, EN. English follows upstream RHI 2.9.4's wording.
namespace RHI.Desktop.Localization;

internal static class SettingsUiStrings
{
    internal static readonly (string Key, string PtBr, string EnUs)[] Entries =
    [
        // The eight sections (upstream keys and order)
        ("Set.SectionGeneral", "Geral", "General"),
        ("Set.SectionGeneralDesc", "Atualizações e como o RHI se comporta no seu PC. Comece aqui se você é novo.", "Updates and how RHI behaves on your PC. Start here if you are new."),
        ("Set.SectionAccounts", "Contas e dados", "Accounts & Data"),
        ("Set.SectionAccountsDesc", "De onde o RHI tira as informações dos mods, e as contas que ele pode usar.", "Where RHI gets its mod information, and the accounts it can use."),
        ("Set.SectionReShade", "ReShade e RenoDX", "ReShade & RenoDX"),
        ("Set.SectionReShadeDesc", "Padrões para todo jogo que usa o ReShade. Cada jogo ainda pode mudá-los no seu próprio ⚙ do ReShade.", "Defaults for every game using ReShade. Each game can still change these in its own ReShade ⚙."),
        ("Set.SectionDisplay", "Tela e HDR", "Display & HDR"),
        ("Set.SectionDisplayDesc", "O que acontece com a sua tela quando você inicia um jogo pelo RHI.", "What happens to your screen when you launch a game through RHI."),
        ("Set.SectionDlss", "DLSS / Streamline", "DLSS / Streamline"),
        ("Set.SectionDlssDesc", "Corresponde à seção DLSS / Streamline do painel de cada jogo.", "Matches the DLSS / Streamline section in each game's panel."),
        ("Set.SectionDriver", "Configurações do driver", "Driver Settings"),
        ("Set.SectionDriverDesc", "Padrões do driver para todo jogo. As configurações do driver de cada jogo têm prioridade.", "Driver defaults for every game. A game's own driver settings override these."),
        ("Set.SectionComponents", "Componentes", "Components"),
        ("Set.SectionComponentsDesc", "Padrões para ReLimiter, OptiScaler e GITC Uplift. O ⚙ de cada jogo tem prioridade.", "Defaults for ReLimiter, OptiScaler and GITC Uplift. Each game's ⚙ overrides these."),
        ("Set.SectionTools", "Ferramentas e manutenção", "Tools & Maintenance"),
        ("Set.SectionToolsDesc", "Ações em lote, pastas e logs, e correções de último recurso.", "Bulk actions, folders and logs, and last-resort fixes."),

        // Find a setting and Tabs
        ("Set.FindLabel", "Encontrar uma configuração", "Find a setting"),
        ("Set.FindPlaceholder", "ex.: tecla, HDR, shaders", "e.g. hotkey, HDR, shaders"),
        ("Set.FindTip", "Procura nos nomes, descrições e dicas das configurações enquanto você digita. Enter procura na hora, Esc limpa.", "Searches setting names, descriptions and tooltips as you type. Enter searches straight away, Esc clears."),
        ("Set.FindResults", "Resultados da busca", "Search results"),
        ("Set.FindNone", "Nenhuma configuração corresponde a \"{0}\"", "No settings match \"{0}\""),
        ("Set.FindOne", "1 configuração corresponde a \"{0}\"", "1 setting matches \"{0}\""),
        ("Set.FindMany", "{0} configurações correspondem a \"{1}\"", "{0} settings match \"{1}\""),
        ("Set.FindCapped", ". Mostrando as primeiras {0}; continue digitando para filtrar.", ". Showing the first {0}; keep typing to narrow it down."),
        ("Set.Tabs", "Abas", "Tabs"),
        ("Set.TabsTip", "Mostra uma seção por vez, com abas no topo. Clique de novo para ver todas as seções numa página só.", "Shows one section at a time, with tabs along the top. Click again for every section on one page."),
        ("Set.Show", "Mostrar", "Show"),
        ("Set.Hide", "Ocultar", "Hide"),

        // General
        ("Set.CardUpdates", "Atualizações", "Updates"),
        ("Set.NextCheckIn", "Próxima verificação em", "Next check in"),
        ("Set.LastChecked", "Última verificação", "Last checked"),
        ("Set.CheckCadenceTip", "O RHI verifica a cada hora com um token do GitHub, a cada 4 horas sem um (o GitHub permite bem menos pedidos sem token), contando entre reinícios.", "RHI checks every hour with a GitHub token, every 4 hours without one (GitHub allows far fewer requests without one), counted across restarts."),
        ("Set.AutomaticUpdates", "Atualizações automáticas", "Automatic updates"),
        ("Set.AutoComponents", "Componentes", "Components"),
        ("Set.AutoComponentsHelp", "ReShade, RenoDX, ReLimiter, Display Commander, OptiScaler, RE Framework e os demais, instalados em segundo plano.", "ReShade, RenoDX, ReLimiter, Display Commander, OptiScaler, RE Framework and the others, installed in the background."),
        ("Set.AutoDlss", "DLSS", "DLSS"),
        ("Set.AutoStreamline", "Streamline", "Streamline"),
        ("Set.OwnUpdatesNote", "As atualizações do próprio RHI nunca são instaladas sem perguntar.", "RHI's own updates are never installed without asking."),
        ("Set.CardWindows", "Janelas", "Windows"),
        ("Set.TrayIcon", "Ícone na bandeja", "Tray icon"),
        ("Set.CloseToTray", "Fechar para a bandeja", "Close to tray"),
        ("Set.CloseToTrayTip", "Fechar a janela mantém o RHI rodando na bandeja do sistema.", "Closing the window keeps RHI running in the system tray."),
        ("Set.CloseToTrayGreyed", "Ligue o ícone na bandeja primeiro: sem ele não há bandeja para onde fechar.", "Turn on the tray icon first: without it there is no tray to close to."),
        ("Set.MotdAtStart", "Mensagem do dia ao iniciar", "Message of the day at start"),

        // Accounts & Data
        ("Set.CardAccounts", "Contas e dados dos mods", "Accounts and mod data"),

        // ReShade & RenoDX
        ("Set.CardKeys", "Teclas, capturas de tela e brilho", "Keys, screenshots and brightness"),
        ("Set.KeysAndScreenshots", "Teclas e capturas de tela", "Keys and screenshots"),
        ("Set.KeysAndScreenshotsTip", "Pasta das capturas de tela e teclas do ReShade aplicadas a todos os reshade.ini gerenciados.", "Screenshot save path and ReShade key bindings applied to all managed reshade.ini files."),
        ("Set.OverlayKey", "Tecla do overlay", "Overlay key"),
        ("Set.OverlayKeyTip", "Abre o overlay do ReShade no jogo.", "Opens the ReShade overlay in game."),
        ("Set.ScreenshotKey", "Tecla de captura de tela", "Screenshot key"),
        ("Set.ScreenshotKeyTip", "A tecla com que o ReShade salva uma captura de tela.", "The key ReShade saves a screenshot with."),
        ("Set.ScreenshotFolderTip", "Onde o ReShade salva as capturas de tela. Digite uma pasta e pressione Enter, ou use Procurar.", "Where ReShade saves screenshots. Type a folder and press Enter, or use Browse."),
        ("Set.ScreenshotBrowseTip", "Escolher uma pasta para as capturas de tela", "Pick a folder for screenshots"),
        ("Set.FolderPerGame", "Pasta por jogo", "Folder per game"),
        ("Set.EffectListStyleTip", "Como os shaders aparecem no overlay do ReShade. Abas agrupa os arquivos de efeito em abas com nome; Árvore mostra uma hierarquia recolhível.", "Controls how shaders are displayed in the ReShade overlay. Tabs groups effect files into named tabs; Tree shows a collapsible hierarchy."),
        ("Set.ApplyToSelectedGame", "Gravar estas no jogo selecionado", "Write these to the selected game"),
        ("Set.ApplyToSelectedGameTip", "Grava as teclas e a pasta de capturas de tela no reshade.ini do jogo selecionado.", "Writes the keys and the screenshot folder into the selected game's reshade.ini."),
        ("Set.ApplyToSelectedGameGreyed", "Selecione um jogo na biblioteca antes de abrir as Configurações para gravar nele.", "Select a game in the library before opening Settings to write to it."),
        ("Set.CardShaders", "Shaders e add-ons", "Shaders and addons"),
        ("Set.DownloadAllShaders", "Baixar todos os shaders", "Download all shaders"),
        ("Set.DownloadAllShadersTip", "Ligado, todos os pacotes de shaders são baixados e guardados em cache. Desligado, os pacotes são baixados quando necessários.", "When On, all shader packs are downloaded and cached. When Off, packs are downloaded on demand when needed."),
        ("Set.WatchAddons", "Vigiar a pasta de add-ons", "Watch the addon folder"),
        ("Set.AddonWatchFolder", "Pasta de add-ons vigiada", "Addon watch folder"),
        ("Set.AddonWatchFolderTip", "O RHI vigia esta pasta procurando arquivos de add-on do RenoDX (.addon64/.addon32) e arquivos compactados. Vazio usa a sua pasta Downloads.", "RHI watches this folder for RenoDX addon files (.addon64/.addon32) and archives. Empty uses your Downloads folder."),
        ("Set.WatchBrowseTip", "Escolher a pasta que o RHI vigia procurando downloads de add-ons", "Pick the folder RHI watches for addon downloads"),
        ("Set.CardReShadeDefaults", "Padrões do ReShade (Linux)", "ReShade defaults (Linux)"),
        ("Set.DefaultChannelTip", "O canal com que o ReShade é instalado em jogos que não escolheram o seu próprio.", "The channel ReShade is installed from in games that have not picked their own."),
        ("Set.ManageIni", "Gerenciar os caminhos de busca do ReShade.ini", "Manage ReShade.ini search paths"),

        // Display & HDR (#312)
        ("Set.CardDisplay", "Tela", "Display"),
        ("Set.WhenAGameStarts", "Quando um jogo inicia", "When a game starts"),
        ("Set.WhenAGameStartsHelp", "No Linux, HDR e resolução são escolhidos por jogo, nas linhas HDR e Resolução da página do jogo, e valem quando você inicia o jogo pelo RHI.", "On Linux, HDR and resolution are chosen per game, in the HDR and Resolution rows of the game's page, and apply when you launch it from RHI."),
        ("Set.HdrButton", "Botão HDR nas páginas dos jogos", "HDR button on game pages"),
        ("Set.HdrButtonTip", "Mostra ou oculta a linha HDR nas páginas dos jogos. Oculta, a escolha de HDR do próprio jogo ainda vale quando você o inicia pelo RHI.", "Show or hide the HDR row on game pages. Hidden, a game's own HDR choice still applies when you launch it from RHI."),
        ("Set.ResButton", "Botão de resolução nas páginas dos jogos", "Resolution button on game pages"),
        ("Set.ResButtonTip", "Mostra ou oculta a linha de troca de resolução nas páginas dos jogos. Oculta, a escolha de resolução do próprio jogo ainda vale quando você o inicia pelo RHI.", "Show or hide the Resolution switch row on game pages. Hidden, a game's own resolution choice still applies when you launch it from RHI."),

        // DLSS / Streamline
        ("Set.CardDefaults", "Padrões", "Defaults"),
        ("Set.AutoDlssWhere", "Atualizações automáticas de DLSS e Streamline: Geral › Atualizações.", "Automatic DLSS and Streamline updates: General › Updates."),
        ("Set.CardBatch", "Implantação em lote", "Batch deploy"),
        ("Set.BatchDeploy", "Implantação em lote", "Batch deploy"),
        ("Set.BatchDeployHelp", "Coloca versões e presets de DLSS e Streamline em vários jogos de uma vez.", "Put DLSS and Streamline versions and presets on many games at once."),
        ("Set.BatchDeployGreyed", "Nenhum jogo da biblioteca tem arquivos de DLSS ou Streamline para trocar, ou o RHI está ocupado.", "No game in the library has DLSS or Streamline files to swap, or RHI is busy."),

        // Driver Settings
        ("Set.CardDriverDefaults", "Padrões do driver", "Driver defaults"),
        ("Set.DriverDefaultsLinux", "No Linux não há perfil de driver da NVIDIA sob Wine/Proton. O equivalente é por jogo: as configurações NVIDIA da seção DLSS / Streamline de cada jogo (presets de DLSS, indicador, Smooth Motion e afins) viram variáveis de inicialização quando você inicia o jogo pelo RHI.", "On Linux there is no NVIDIA driver profile under Wine/Proton. The equivalent is per game: each game's NVIDIA settings in its DLSS / Streamline section (DLSS presets, indicator, Smooth Motion and so on) become launch variables when you launch it from RHI."),

        // Components
        ("Set.CardReLimiterOptiScaler", "ReLimiter e OptiScaler", "ReLimiter and OptiScaler"),

        ("Set.WritePeakToAll", "Gravar o brilho máximo em todos os jogos", "Write peak brightness to all games"),
        ("Set.WriteReLimiterToAll", "Gravar em todos os jogos com ReLimiter", "Write to all ReLimiter games"),
        ("Set.WriteOsKeyToAll", "Gravar a tecla em todos os jogos com OptiScaler", "Write key to all OptiScaler games"),

        // Tools & Maintenance
        ("Set.CardTools", "Ferramentas", "Tools"),
        ("Set.DeployToMany", "Implantar em vários jogos", "Deploy to many games"),
        ("Set.FoldersAndLogs", "Pastas e logs", "Folders and logs"),
        ("Set.FoldersAndLogsTip", "Onde o RHI guarda as configurações, o cache e os dados. A pasta de logs ajuda a resolver problemas.", "Where RHI keeps its settings, cache and data. The logs folder helps with troubleshooting."),
        ("Set.OpenConfigurationTip", "Abrir a pasta de configuração do RHI", "Open RHI's configuration folder"),
        ("Set.OpenCacheTip", "Abrir a pasta de cache do RHI (downloads)", "Open RHI's cache folder (downloads)"),
        ("Set.OpenDataTip", "Abrir a pasta de dados do RHI", "Open RHI's data folder"),
        ("Set.CardTroubleshooting", "Solução de problemas", "Troubleshooting"),
        ("Set.TroubleshootingDesc", "Use estes se algo der muito errado. Cada um pergunta antes, com Cancelar como padrão.", "Use these if something is badly wrong. Each asks first, with Cancel as the default."),
        ("Set.FullRefresh", "Atualização completa", "Full refresh"),
        ("Set.FullRefreshTip", "Procura de novo os jogos em todas as lojas e pastas e relê os arquivos de cada jogo. Use se faltam jogos, se pastas mudaram ou se o DLSS foi adicionado a um jogo.", "Scans every store and folder for games again and re-reads each game's files. Use it if games are missing, folders have changed, or DLSS was added to a game."),
        ("Set.FullRefreshAsk", "Procurar todos os jogos de novo? Pode levar um tempo; nada instalado nos jogos muda.", "Scan for every game again? It can take a while; nothing installed in games changes."),
        ("Set.FullRefreshStarted", "Atualização completa iniciada: a janela principal mostra o andamento.", "Full refresh started: the main window shows its progress."),
        ("Set.ClearCacheAsk", "Limpar o cache do RHI-Linux? Pacotes e shaders baixados são apagados e baixados de novo quando necessários. Componentes instalados nos jogos não mudam.", "Clear RHI-Linux's cache? Downloaded packages and shaders are deleted and downloaded again when needed. Components installed in games are not changed."),
        ("Set.ResetSettingsTip", "Volta todas as configurações aos padrões. Não toca em nada instalado nos jogos.", "Puts every setting back to its default. Nothing installed in games is touched."),
        ("Set.ResetSettingsAsk", "Voltar todas as configurações aos padrões? Isto não pode ser desfeito. Nada instalado nos jogos muda.", "Put every setting back to its default? This can't be undone. Nothing installed in games changes."),

        // #302: upstream 2.8.9's tooltip sweep (main window)
        ("Tip.FilterAll", "Todos os jogos encontrados, exceto os ocultos.", "Every game found, except hidden ones."),
        ("Tip.FilterFavourites", "Só os jogos marcados como favoritos.", "Only the games you marked as favourites."),
        ("Tip.FilterInstalled", "Jogos com um componente instalado pelo RHI.", "Games with a component RHI installed."),
        ("Tip.FilterUnreal", "Jogos em Unreal Engine.", "Unreal Engine games."),
        ("Tip.FilterUnity", "Jogos em Unity.", "Unity games."),
        ("Tip.FilterOther", "Jogos em outros motores, ou de motor desconhecido.", "Games on other engines, or an unknown engine."),
        ("Tip.FilterRenoDx", "Jogos que têm um mod HDR do RenoDX.", "Games that have a RenoDX HDR mod."),
        ("Tip.FilterLuma", "Jogos que têm um mod do Luma Framework.", "Games that have a Luma Framework mod."),
        ("Tip.FilterHidden", "Jogos que você ocultou. Mostrar um jogo o traz de volta à lista.", "Games you hid. Showing a game brings it back to the list."),
        ("Tip.SectionRuntime", "O que o RHI detectou e o que você escolheu para este jogo: executável, prefixo Wine/Proton, API gráfica, HDR e o ReShade do jogo.", "What RHI detected and what you chose for this game: executable, Wine/Proton prefix, graphics API, HDR and the game's ReShade."),
        ("Tip.SectionDlss", "As DLLs de DLSS e Streamline do jogo: versões, trocas e presets.", "The game's DLSS and Streamline DLLs: versions, swaps and presets."),
        ("Tip.SectionNeural", "O método de Renderização Neural recomendado, as versões de cada ferramenta e da DLL do NR.", "The recommended Neural Rendering method, and the versions of each tool and of the NR DLL."), // w14/layouts-a
        ("Tip.SectionDriver", "As opções do driver deste jogo; no Linux, o limite de FPS pelo DXVK / VKD3D-Proton.", "This game's driver options; on Linux, the FPS limit through DXVK / VKD3D-Proton."), // w14/layouts-a
        ("Tip.SectionComponents", "Os componentes que o RHI instala, atualiza e remove neste jogo.", "The components RHI installs, updates and removes in this game."),
        ("Tip.SectionExtras", "Desbloqueios de geração de quadros, ASI Loader e outros extras.", "Frame generation unlocks, ASI Loader and other extras."),
        ("Tip.SectionSummary", "O que a seção recolhida contém. API = API gráfica forçada; os números são as versões instaladas.", "What the collapsed section holds. API = forced graphics API; the numbers are the installed versions."),
        ("Tip.DlssSummary", "Padrões que Aplicar padrões coloca no jogo. SR = Super Resolution (nvngx_dlss.dll), RR = Ray Reconstruction (nvngx_dlssd.dll), FG = Frame Generation (nvngx_dlssg.dll), NR = Neural Rendering (nvngx_dlssnr.dll), SL = Streamline; — = não muda.", "The defaults Apply defaults puts on the game. SR = Super Resolution (nvngx_dlss.dll), RR = Ray Reconstruction (nvngx_dlssd.dll), FG = Frame Generation (nvngx_dlssg.dll), NR = Neural Rendering (nvngx_dlssnr.dll), SL = Streamline; — = not changed."),
        ("Tip.GraphicsApi", "A API gráfica que o jogo usa (DirectX 9/11/12, Vulkan, OpenGL), detectada no executável ou nos dados do RHI. Decide qual DLL do ReShade é usada.", "The graphics API the game uses (DirectX 9/11/12, Vulkan, OpenGL), detected from its executable or RHI's data. It decides which ReShade DLL is used."),
        ("Tip.EngineArchitecture", "O motor do jogo e se o executável é de 32 ou 64 bits. Componentes de 64 bits não carregam num jogo de 32 bits.", "The game's engine, and whether its executable is 32-bit or 64-bit. 64-bit components don't load in a 32-bit game."),
    ];
}
