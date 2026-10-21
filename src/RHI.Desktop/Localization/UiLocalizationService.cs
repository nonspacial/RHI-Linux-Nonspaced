using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;

using RtxEncoreText = RHI.Platform.Linux.Deployment.RtxEncoreDeploymentService;

namespace RHI.Desktop.Localization;

public static class UiLocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> PtBr = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Ui.WindowTitle"] = "RHI-Linux — Jogos de PC simplificados",
        ["Ui.Tagline"] = "Jogos de PC simplificados · Linux",
        ["Ui.QuickStart"] = "Início rápido",
        ["Ui.AvailableHdrMods"] = "Mods HDR disponíveis",
        ["Ui.Refresh"] = "Atualizar",
        ["Ui.AddGame"] = "Adicionar jogo",
        ["Ui.ShadersAddons"] = "Shaders/Add-ons",
        ["Ui.UpdateAll"] = "Atualizar tudo",
        ["Ui.UpdateAllSummary"] = "Atualizar tudo: {0} atualizado(s) · {1} em dia · {2} adiado(s) · {3} com falha",
        ["Ui.UpdateAllUnchecked"] = " · {0} não verificado(s)",
        ["Ui.UpdateAllFirstFailure"] = " Falha: {0} ({1}) — {2}",
        ["Ui.UpdateAllMoreFailures"] = " (+{0} outra(s))",
        ["Ui.UpdateAllBlocked"] = "atualizações bloqueadas até Editar jogo confirmar o executável do jogo",
        ["Ui.ShaderAddonApplied"] = "Shaders/Add-ons aplicados: {0} pacote(s) de shaders, {1} add-on(s).",
        ["Ui.ReShadeCachedCopyUsed"] = " O reshade.me estava indisponível, então a cópia do ReShade em cache foi usada.",
        ["Ui.UpdateAllNone"] = "Nenhum componente gerenciado pelo RHI participa de Atualizar tudo.",
        ["Ui.BatchDlss"] = "DLSS em lote",
        ["Ui.Links"] = "Links",
        ["Ui.Motd"] = "MOTD",
        ["Ui.UpdateHistory"] = "Atualizações",
        ["Ui.Donate"] = "Doar",
        ["Ui.Settings"] = "Configurações",
        ["Ui.LibraryToggle"] = "☰ Biblioteca",
        ["Ui.LibraryToggleTip"] = "Mostrar a biblioteca de jogos",
        ["Ui.MoreActions"] = "Mais ações",
        ["Ui.ViewDetail"] = "Visualização detalhada",
        ["Ui.ViewCompact"] = "Visualização compacta",
        ["Ui.SearchGames"] = "Filtrar jogos...",
        ["Ui.Library"] = "BIBLIOTECA",
        ["Ui.All"] = "Todos",
        ["Ui.Favourites"] = "Favoritos",
        ["Ui.Installed"] = "Instalados",
        ["Ui.Other"] = "Outros",
        ["Ui.Hidden"] = "Ocultos",
        ["Ui.Games"] = "jogos",
        ["Ui.Edit"] = "Editar",
        ["Ui.RemoveManual"] = "Remover manual",
        ["Ui.OpenFolder"] = "Abrir pasta",
        ["Ui.OpenPrefix"] = "Abrir prefixo",
        ["Ui.Play"] = "Jogar",
        ["Ui.Show"] = "Mostrar",
        ["Ui.Hide"] = "Ocultar",
        ["Ui.FavouriteOn"] = "★ Favorito",
        ["Ui.FavouriteOff"] = "☆ Favorito",
        ["Ui.ExecutionGame"] = "Execução / Jogo",
        ["Ui.ExecutionGameNote"] = "Destino de execução, prefixo Wine/Proton e detecção da API gráfica.",
        ["Ui.Launcher"] = "Inicialização / launcher",
        ["Ui.TargetSafety"] = "Segurança do alvo",
        ["Ui.GameRoot"] = "Raiz do jogo / patches",
        ["Ui.Prefix"] = "Prefixo Wine / Proton",
        ["Ui.TargetExe"] = "Executável alvo",
        ["Ui.PatchDirectory"] = "Diretório de binários/patches",
        ["Ui.GraphicsApi"] = "API gráfica",
        ["Ui.ForceApi"] = "Forçar API",
        ["Ui.LumaMode"] = "Modo Luma",
        ["Ui.HdrExecution"] = "Execução HDR",
        ["Ui.NeuralRendering"] = "Renderização neural",
        ["Ui.EngineArchitecture"] = "Motor / arquitetura",
        ["Ui.ApiEvidence"] = "Evidência da API",
        ["Ui.DlssStreamline"] = "DLSS / Streamline",
        ["Ui.GameDlls"] = "DLLs locais do jogo",
        ["Ui.SuperResolution"] = "Super-resolução",
        ["Ui.RayReconstruction"] = "Reconstrução de raios",
        ["Ui.FrameGeneration"] = "Geração de quadros",
        ["Ui.StreamlineUpdate"] = "Streamline — Atualização",
        ["Ui.StreamlineUpdateNote"] = "Atualiza e restaura DLLs NVIDIA Streamline já existentes no jogo. Separadamente, o OptiScaler Nightly/DLSS NR/F5 DLSSNR pode implantar sua própria pilha Streamline + DLSS Enabler, como no RHI Windows.",
        ["Ui.StreamlineDetectedNote"] = "Integração Streamline detectada. Use esta seção para atualizar/restaurar as DLLs existentes; o deploy privado do OptiScaler fica nas configurações avançadas do OptiScaler Nightly/DLSS NR/F5 DLSSNR.",
        ["Ui.StreamlineMissingNote"] = "Streamline nativo não detectado neste jogo. Esta linha não cria uma integração nativa; porém o OptiScaler Nightly/DLSS NR/F5 DLSSNR pode implantar Streamline + DLSS Enabler em OptiScaler/, igual ao RHI Windows.",
        ["Ui.StatusDetected"] = "Detectado",
        ["Ui.StatusNotDetected"] = "Não detectado",
        ["Ui.ReShadeProfile"] = "Perfil do ReShade",
        ["Ui.ManagedComponents"] = "Componentes gerenciados",
        ["Ui.LinuxBackendActive"] = "Integração Linux ativa",
        ["Ui.Extras"] = "Extras",
        ["Ui.CheckUpdate"] = "Verificar atualização",
        ["Ui.Install"] = "Instalar",
        ["Ui.Remove"] = "Remover",
        ["Ui.Apply"] = "Aplicar",
        ["Ui.Restore"] = "Restaurar",
        ["Ui.Browse"] = "Procurar…",
        ["Ui.Clear"] = "Limpar",
        ["Ui.SettingsWindowTitle"] = "RHI-Linux — Configurações",
        ["Ui.Language"] = "Idioma",
        ["Ui.LanguageNote"] = "A alteração é aplicada imediatamente à janela principal e fica salva no XDG. Reabra Configurações para atualizar todos os textos desta janela.",
        ["Ui.Save"] = "Salvar",
        ["Ui.ResetSettings"] = "Restaurar configurações",
        ["Ui.UxTab"] = "UX",
        ["Ui.MaintenanceTab"] = "Manutenção",
        ["Ui.Maintenance"] = "Manutenção / caminhos XDG",
        ["Ui.DesktopExperience"] = "Experiência de desktop Linux",
        ["Ui.ViewMode"] = "Modo de visualização",
        ["Ui.QuickStartTip"] = "Guia inicial do Linux e regras de responsabilidade dos componentes",
        ["Ui.AvailableHdrModsTip"] = "Todos os jogos com mod RenoDX ou Luma nos catálogos, com link de download",
        ["Ui.AddGameTip"] = "Adiciona manualmente um executável de jogo do Windows à biblioteca persistente",
        ["Ui.BatchDlssTip"] = "Aplica ou restaura versões locais de DLSS/Streamline em vários jogos detectados",
        ["Ui.MotdTip"] = "Mostra a mensagem do dia do RHI-Linux",
        ["Ui.UpdateHistoryTip"] = "Histórico de instalações, atualizações e remoções de componentes",
        ["Ui.DonateTip"] = "Apoie os autores dos mods",
        ["Ui.ViewModeTip"] = "Alterna entre visualização detalhada e compacta",
        ["Ui.DetectedLinuxTip"] = "Detectado no Linux",
        ["Ui.EditTip"] = "Separar o launcher usado para iniciar do executável real que recebe detecção, mods e patches",
        ["Ui.RemoveManualTip"] = "Remove somente a entrada manual salva; os arquivos do jogo não são alterados",
        ["Ui.RepairMove"] = "Reparar após mover",
        ["Ui.RepairMoveTip"] = "O jogo mudou de pasta e os registros ou configurações do RHI ainda apontam para a antiga. Confere a nova pasta e atualiza registros e configurações; nada muda se algo não bater.",

        ["Ui.DlssNote"] = "O gerenciador local substitui apenas DLLs de DLSS/Streamline que o próprio jogo já fornece. Jogos sem DLSS usam o DLSS5 Feeder Full; trocas diretas ficam bloqueadas enquanto o Feeder Full controla sua pilha privada de SR/NR. Aplicar padrões usa as versões salvas; um padrão vazio significa não alterar o componente. O selo ✓ RHI aplicado indica que existe backup .original do RHI e a restauração está disponível.",
        ["Ui.ReShadeProfileNote"] = "Canais Stable, Nightly, Legacy fixado, Sem add-ons e DLL personalizada local. O INI e os pacotes de shaders do RHI são atualizados sem assumir controle de instalações externas.",
        ["Ui.ManagedProfileControls"] = "Controles do perfil gerenciado pelo RHI",
        ["Ui.Channel"] = "Canal",
        ["Ui.LegacyVersion"] = "Versão legada",
        ["Ui.LegacyVersionNote"] = "Fixado: canais legados nunca são atualizados automaticamente.",
        ["Ui.CustomDll"] = "DLL personalizada",
        ["Ui.CustomDllPlaceholder"] = "Selecione uma DLL do ReShade ou digite o caminho",
        ["Ui.D3DCompilerPlaceholder"] = "Detectar no prefixo ou selecionar Microsoft D3DCompiler_47.dll",
        ["Ui.D3DCompilerNote"] = "Os dois auxiliares instalam o d3dcompiler_47 nativo da Microsoft e o RHI aplica d3dcompiler_47=native,builtin apenas ao jogo selecionado. O Feeder Full aceita o compilador do prefixo sem diferenciar maiúsculas/minúsculas e pode usar diretamente a DLL da Microsoft selecionada; Aplicar continua salvando-a no perfil do ReShade. O RHI nunca redistribui essa DLL da Microsoft.",
        ["Ui.FeederConsumer"] = "Consumidor do Feeder",
        ["Ui.FeederRenoDxNote"] = "RenoDX DLSS5 é o consumidor neural padrão do Feeder. O RHI baixa a versão compatível automaticamente; não instala Deep Fried Chicken nem adiciona LoadFromDllMain do Chicken.",
        ["Ui.FeederPackagePlaceholder"] = "Selecione o pacote oficial Deep Fried Chicken (.zip ou .7z) antes de instalar o Feeder Full",
        ["Ui.FeederPackageNote"] = "O Feeder Full usa RenoDX DLSS5 como consumidor neural padrão e mantém Deep Fried Chicken como alternativa manual. Com RenoDX DLSS5, o RHI baixa o consumidor dedicado automaticamente. Com DFC, o pacote oficial é validado pelo SHA256SUMS.txt e pacotes AES conhecidos usam a senha pública do upstream sem persistência. Apenas um consumidor neural pode ser usado por instalação.",
        ["Ui.QuickPackages"] = "Pacotes rápidos:",
        ["Ui.XdgSettingsNote"] = "As configurações são nativas do XDG e incluem monitor de add-ons opcional, visualização detalhada/compacta, bandeja/fechar para a bandeja e preferências de MOTD. Controles exclusivos do Windows, como NVAPI e perfis de driver, permanecem fora do RHI-Linux.",
        ["Ui.ManagedComponentsNote"] = "ReShade, RenoDX HDR, RenoDX Upgrade, OptiScaler, RE Framework, ReLimiter, Display Commander e add-ons por jogo ficam disponíveis em alvos Wine/Proton compatíveis. A tradução DirectX continua sob responsabilidade do runner no Linux. Instalações externas permanecem protegidas contra remoção.",
        ["Ui.ExtrasNote"] = "Integrações extras · orquestração do UAL e ShortFuse.",
        ["Ui.PerGameOrchestration"] = "Orquestração por jogo",
        ["Ui.LinuxPolicyNote"] = "Política Linux: a tradução DirectX pertence ao runner Wine/Proton selecionado; o RHI-Linux não instala, atualiza, substitui nem remove DLLs da camada de tradução, exceto o DX9 HDR, que você adiciona por jogo pela linha dele. O HDR apenas prepara um perfil de execução por jogo. Launchers de lojas devem aplicar esse perfil; o RHI não encapsula o processo do launcher.",
        ["Ui.UalPolicyNote"] = "O Ultimate ASI Loader baixa a versão oficial correspondente à arquitetura, valida o SHA-256 publicado pelo GitHub quando disponível, instala ao lado do executável selecionado, aplica o override Wine native,builtin por jogo e usa encadeamento <nome>Hooked.dll apenas para DLLs existentes que não pertençam ao RHI. A remoção restaura o original encadeado e o override Wine anterior. Use injeção ASI somente em títulos single-player/offline, salvo quando a política anti-cheat do jogo permitir explicitamente.",
        ["Ui.Processing"] = "Processando…",
        ["Ui.CancelOperation"] = "Cancelar",
        ["Ui.LegacyWsi"] = "WSI legado",
        ["Ui.CopyLaunchProfile"] = "Copiar perfil de execução",
        ["Ui.ApplyDefaults"] = "Aplicar padrões",
        ["Ui.RestoreAll"] = "Restaurar tudo",
        ["Ui.ApplyUpdate"] = "Aplicar / Atualizar",
        ["Ui.InstallProtontricks"] = "Instalar via Protontricks",
        ["Ui.ManageReShadeIni"] = "Gerenciar / mesclar ReShade.ini",
        ["Ui.OfficialLegacy"] = "Oficial legado",
        ["Ui.FullShaderManager"] = "Gerenciador completo de Shaders/Add-ons…",
        ["Ui.AutoAsiConfig"] = "⚙ Configuração ASI automática (opcional)",
        ["Ui.DragSectionTip"] = "Arraste para reordenar a seção",
        ["Ui.CollapseExpandTip"] = "Recolher / expandir",
        ["Ui.LegacyWsiTip"] = "Adiciona apenas ENABLE_HDR_WSI=1. Mantenha desativado em Mesa/NVIDIA modernos, salvo quando o caminho HDR WSI realmente exigir essa camada de compatibilidade.",
        ["Ui.ApplyDefaultsTip"] = "Aplica somente as versões padrão configuradas de DLSS/Streamline aos alvos locais já existentes no jogo",
        ["Ui.RenoDxSettingsTip"] = "Configurações de compatibilidade do RenoDX",
        ["Ui.OptiScalerSettingsTip"] = "Configurações avançadas do OptiScaler",
        ["Ui.Dlssg2030SettingsTip"] = "RTX 20 FG Unlock legado — DLL proxy",
        ["Ui.Dlssg2030Title"] = "RTX 20 FG Unlock — Legado/Experimental",
        ["Ui.Dlssg2030Intro"] = "Caminho legado SM75 para RTX 20 em jogos Windows x64 DirectX 12 sob Wine/Proton. Novas instalações RTX 30 foram movidas para Universal MFG. O RHI mantém este caminho para RTX 20 e para remover/atualizar registros antigos com segurança.",
        ["Ui.Dlssg2030RouteNote"] = "Novas instalações usam somente RTX 20 / Router=SM75. RTX 30 / SM86 foi aposentado neste caminho e agora usa Universal MFG. Registros RTX 30 antigos continuam removíveis e atualizáveis para preservar restauração e remoção seguras.",
        ["Ui.Dlssg2030Gpu"] = "Geração da GPU",
        ["Ui.Dlssg2030Proxy"] = "DLL proxy",
        ["Ui.Dlssg2030Check"] = "Verificar atualização",
        ["Ui.Dlssg2030Update"] = "Atualizar",
        ["Ui.Dlssg2030CompatibilityNote"] = "Compatibilidade experimental: confirme em jogo real que o proxy SM75 é carregado sem crash, tela preta ou perda do dispositivo e que a Geração de Quadros funciona. Para RTX 30, use o Universal MFG.",
        ["Ui.DlssEnablerStandaloneSettingsTip"] = "DLSS Enabler Standalone — DLL proxy e atualização",
        ["Ui.DlssEnablerStandaloneTitle"] = "DLSS Enabler Standalone",
        ["Ui.DlssEnablerStandaloneIntro"] = "Instala o DLSS Enabler como DLL proxy diretamente ao lado do executável alvo, independente do OptiScaler. O RHI protege slots existentes, valida o pacote Windows x64 e aplica native,builtin somente ao jogo selecionado.",
        ["Ui.DlssEnablerStandaloneProxy"] = "DLL proxy",
        ["Ui.DlssEnablerStandaloneCheck"] = "Verificar atualização",
        ["Ui.DlssEnablerStandaloneUpdate"] = "Atualizar",
        ["Ui.DlssEnablerStandaloneCompatibilityNote"] = "Compatibilidade experimental: confirme em jogo real sob Wine/Proton que o proxy escolhido é carregado sem crash, tela preta ou perda do dispositivo.",
        ["Ui.Rtx40MfgSettingsTip"] = "Universal MFG — geração da GPU, DLL proxy e atualização",
        ["Ui.RtxEncoreSettingsTip"] = "RTX Encore — nome do arquivo, Neural Rendering e atualização",
        ["Ui.Rtx40MfgTitle"] = "Universal MFG — Experimental",
        ["Ui.Rtx40MfgIntro"] = "Instala o Universal RTXMFG como uma única DLL proxy ao lado do executável alvo. RTX 40 usa DX12 e Vulkan experimental; RTX 30 usa somente DX12 e permanece experimental upstream. Exige uma pilha funcional de Streamline DLSS Frame Generation, fornecida pelo jogo ou pelo fluxo OptiScaler Nightly/DLSS NR/F5 DLSSNR + Streamline/DLSS Enabler. O RHI valida SHA-256/PE x64 e protege slots existentes.",
        ["Ui.Rtx40MfgGpuGeneration"] = "Geração da GPU",
        ["Ui.Rtx40MfgProxy"] = "DLL proxy",
        ["Ui.Rtx40MfgCompatibility"] = "RTX 40: DirectX 12 principal e Vulkan experimental. RTX 30: DirectX 12 apenas e suporte ainda muito experimental upstream. RTX 20 permanece no caminho legado SM75. Proxies Bink continuam bloqueados até existir chaining/rollback dedicado. Backspace abre o menu do RTXMFG.",
        ["Ui.Rtx40MfgCheck"] = "Verificar atualização",
        ["Ui.Rtx40MfgUpdate"] = "Atualizar",
        ["Ui.Rtx40MfgCompatibilityNote"] = "Compatibilidade experimental: valide em hardware real sob Wine/Proton. O menu deve abrir, Frame Generation/MFG deve funcionar e não deve haver crash, congelamento, tela preta, perda do dispositivo ou regressão de apresentação.",
        ["Ui.ProxyNameTip"] = "Nome da DLL proxy. As opções recomendadas são version.dll, winmm.dll e winhttp.dll.",
        ["Ui.ShortFuseAutoTip"] = "Quando ativado: ReShade → ReShade64.asi, UAL automático winmm→version→dinput8, HookStreamline=1 e HookDirectX=1.",
        ["Ui.ShortFuseZzzLoadOrder"] = "Ordem de carga ZZZ",
        ["Ui.ShortFuseZzzTip"] = "Quando ativado, o add-on do ShortFuse é implantado como zzz_renodx-dlss.addon64 para carregar por último na ordem de add-ons do ReShade (o Wine/Proton lista os arquivos em ordem alfabética, como o Windows). Necessário em alguns jogos em que outros add-ons devem inicializar antes da DLSS Tool. Se o ShortFuse já estiver instalado, o arquivo é renomeado imediatamente.",
        ["Ui.NrVersions"] = "Versões NR",
        ["Ui.LatestPinNote"] = "Mais recente usa a release mais nova; uma tag fixa mantém esse add-on nessa versão para este jogo. Com o add-on instalado, a troca substitui só o arquivo do add-on.",
        ["Ui.LoadHistory"] = "Atualizar versões",
        ["Ui.NrDllVersionNote"] = "Versão do nvngx_dlssnr.dll usada ao instalar ShortFuse ou Feeder; com um deles instalado, a troca substitui só essa DLL.",
        ["Ui.Official"] = "Oficial",
        ["Ui.WineProtonProxy"] = "Proxy Wine / Proton",
        ["Ui.OptionStable"] = "Estável",
        ["Ui.OptionNightly"] = "Desenvolvimento",
        ["Ui.OptionLegacy"] = "Legado",
        ["Ui.OptionCustom"] = "Personalizado",
        ["Ui.OptionAutomatic"] = "Automático",
        ["Ui.OptionEnabled"] = "Ativado",
        ["Ui.OptionDisabled"] = "Desativado",
        ["Ui.OptionLatest"] = "Mais recente",
        ["Ui.HdrOff"] = "Desativado",
        ["Ui.HdrNativeWayland"] = "Wayland nativo",
        ["Ui.HdrGamescope"] = "Gamescope",
        ["Ui.ProductRuntime"] = ".NET 8 / Avalonia nativo",
        ["Ui.Starting"] = "Iniciando RHI-Linux…",
        ["Ui.SelectGame"] = "Selecione um jogo",
        ["Ui.UnknownRunner"] = "executor desconhecido",
        ["Ui.NotCheckedYet"] = "Última versão ainda não verificada.",
        ["Ui.ReShadeApplyHint"] = "Selecione um canal e use Aplicar / Atualizar. Estável é o padrão.",
        ["Ui.ReShadeGlobalDefault"] = "Selecione um canal e use Aplicar / Atualizar. Padrão global: {0}.",
        ["Ui.StatusInstalled"] = "Instalado",
        ["Ui.StatusExternal"] = "externo",
        ["Ui.ModAuthorBy"] = "por {0}",
        ["Ui.StatusUpdateAvailable"] = "Atualização disponível",
        ["Ui.StatusUnsupported"] = "Não compatível",
        ["Ui.StatusAvailable"] = "Disponível",
        ["Ui.StatusUnknown"] = "Desconhecido",
        ["Ui.ActionInstall"] = "Instalar",
        ["Ui.ActionRemove"] = "Remover",
        ["Ui.CurrentHotkey"] = "Combinação atual: {0}",
        ["Ui.UnknownExistingKey"] = "Tecla existente (não reconhecida)",
        ["Ui.ReShadeInternalFormat"] = "Formato interno do ReShade: {0}\nOrdem interna: tecla, Ctrl, Shift, Alt.",
        ["Ui.UnlocalizedPlatformMessage"] = "O componente retornou uma mensagem técnica ainda não traduzida para PT-BR; confira o texto original em {0}.",
        ["Ui.UnexpectedError"] = "Algo deu errado: {0} O RHI continua em execução; os detalhes estão em {1}.",
        ["Ui.UnexpectedErrorNoLog"] = "Algo deu errado: {0} O RHI continua em execução, mas os detalhes não puderam ser gravados no log.",
        ["Ui.RepeatedErrorsRestart"] = "O RHI encontrou erros repetidos e parou de exibi-los; os detalhes estão em {0}. Reinicie o RHI.",
        ["Ui.SaveFailedSessionOnly"] = "Não foi possível salvar {0}: {1} A alteração vale só até o RHI ser fechado.",
        ["Ui.SaveFailedNothingChanged"] = "Não foi possível salvar {0}: {1} Nada foi alterado.",
        ["Ui.WhatGameLibrary"] = "a biblioteca de jogos",
        ["Ui.WhatSettings"] = "as configurações",
        ["Ui.WhatHdrProfile"] = "o perfil de inicialização HDR",
        ["Ui.WhatGamePreferences"] = "as preferências deste jogo",
        ["Ui.WhatDeploymentRecords"] = "os registros de componentes do RHI",
        ["Ui.WhatShaderPackSelection"] = "a seleção de arquivos do pacote de shaders",
        ["Ui.CacheNotCleared"] = "O cache não foi totalmente limpo: {0}",
        ["Ui.FolderNotOpened"] = "Não foi possível abrir a pasta: {0}"
    };

    private static readonly IReadOnlyDictionary<string, string> EnUs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Ui.WindowTitle"] = "RHI-Linux — Simplified PC gaming",
        ["Ui.Tagline"] = "Simplified PC gaming · Linux",
        ["Ui.QuickStart"] = "Quick start",
        ["Ui.AvailableHdrMods"] = "Available HDR mods",
        ["Ui.Refresh"] = "Refresh",
        ["Ui.AddGame"] = "Add game",
        ["Ui.ShadersAddons"] = "Shaders/Add-ons",
        ["Ui.UpdateAll"] = "Update all",
        ["Ui.UpdateAllSummary"] = "Update All: {0} updated · {1} current · {2} deferred · {3} failed",
        ["Ui.UpdateAllUnchecked"] = " · {0} could not be checked",
        ["Ui.UpdateAllFirstFailure"] = " Failed: {0} ({1}) — {2}",
        ["Ui.UpdateAllMoreFailures"] = " (+{0} more)",
        ["Ui.UpdateAllBlocked"] = "updates blocked until Edit game confirms the game executable",
        ["Ui.ShaderAddonApplied"] = "Shaders/Add-ons applied: {0} shader pack(s), {1} add-on(s).",
        ["Ui.ReShadeCachedCopyUsed"] = " reshade.me was unavailable, so the cached ReShade copy was used.",
        ["Ui.UpdateAllNone"] = "No RHI-managed Update All components were found.",
        ["Ui.BatchDlss"] = "Batch DLSS",
        ["Ui.Links"] = "Links",
        ["Ui.Motd"] = "MOTD",
        ["Ui.UpdateHistory"] = "Updates",
        ["Ui.Donate"] = "Donate",
        ["Ui.Settings"] = "Settings",
        ["Ui.LibraryToggle"] = "☰ Library",
        ["Ui.LibraryToggleTip"] = "Show the game library",
        ["Ui.MoreActions"] = "More actions",
        ["Ui.ViewDetail"] = "Detailed view",
        ["Ui.ViewCompact"] = "Compact view",
        ["Ui.SearchGames"] = "Filter games...",
        ["Ui.Library"] = "LIBRARY",
        ["Ui.All"] = "All",
        ["Ui.Favourites"] = "Favourites",
        ["Ui.Installed"] = "Installed",
        ["Ui.Other"] = "Other",
        ["Ui.Hidden"] = "Hidden",
        ["Ui.Games"] = "games",
        ["Ui.Edit"] = "Edit",
        ["Ui.RemoveManual"] = "Remove manual entry",
        ["Ui.OpenFolder"] = "Open folder",
        ["Ui.OpenPrefix"] = "Open prefix",
        ["Ui.Play"] = "Play",
        ["Ui.Show"] = "Show",
        ["Ui.Hide"] = "Hide",
        ["Ui.FavouriteOn"] = "★ Favourite",
        ["Ui.FavouriteOff"] = "☆ Favourite",
        ["Ui.ExecutionGame"] = "Launch / Game",
        ["Ui.ExecutionGameNote"] = "Launch target, Wine/Proton prefix and graphics API detection.",
        ["Ui.Launcher"] = "Launch / launcher",
        ["Ui.TargetSafety"] = "Target safety",
        ["Ui.GameRoot"] = "Game root / patches",
        ["Ui.Prefix"] = "Wine / Proton prefix",
        ["Ui.TargetExe"] = "Target executable",
        ["Ui.PatchDirectory"] = "Binaries / patch directory",
        ["Ui.GraphicsApi"] = "Graphics API",
        ["Ui.ForceApi"] = "Force API",
        ["Ui.LumaMode"] = "Luma mode",
        ["Ui.HdrExecution"] = "HDR launch",
        ["Ui.NeuralRendering"] = "Neural rendering",
        ["Ui.EngineArchitecture"] = "Engine / architecture",
        ["Ui.ApiEvidence"] = "API evidence",
        ["Ui.DlssStreamline"] = "DLSS / Streamline",
        ["Ui.GameDlls"] = "Game-local DLLs",
        ["Ui.SuperResolution"] = "Super resolution",
        ["Ui.RayReconstruction"] = "Ray reconstruction",
        ["Ui.FrameGeneration"] = "Frame generation",
        ["Ui.StreamlineUpdate"] = "Streamline — Update",
        ["Ui.StreamlineUpdateNote"] = "Updates and restores NVIDIA Streamline DLLs already present in the game. Separately, OptiScaler Nightly/DLSS NR/F5 DLSSNR can deploy its own Streamline + DLSS Enabler stack, matching Windows RHI.",
        ["Ui.StreamlineDetectedNote"] = "Streamline integration detected. Use this section to update/restore the existing DLLs; OptiScaler private deployment is available in the advanced settings for Nightly/DLSS NR/F5 DLSSNR.",
        ["Ui.StreamlineMissingNote"] = "Native Streamline was not detected in this game. This row does not create a native integration; however OptiScaler Nightly/DLSS NR/F5 DLSSNR can deploy Streamline + DLSS Enabler under OptiScaler/, matching Windows RHI.",
        ["Ui.StatusDetected"] = "Detected",
        ["Ui.StatusNotDetected"] = "Not detected",
        ["Ui.ReShadeProfile"] = "ReShade profile",
        ["Ui.ManagedComponents"] = "Managed components",
        ["Ui.LinuxBackendActive"] = "Linux integration active",
        ["Ui.Extras"] = "Extras",
        ["Ui.CheckUpdate"] = "Check for update",
        ["Ui.Install"] = "Install",
        ["Ui.Remove"] = "Remove",
        ["Ui.Apply"] = "Apply",
        ["Ui.Restore"] = "Restore",
        ["Ui.Browse"] = "Browse…",
        ["Ui.Clear"] = "Clear",
        ["Ui.SettingsWindowTitle"] = "RHI-Linux — Settings",
        ["Ui.Language"] = "Language",
        ["Ui.LanguageNote"] = "The change is applied immediately to the main window and is saved in XDG. Reopen Settings to refresh every label in this dialog.",
        ["Ui.Save"] = "Save",
        ["Ui.ResetSettings"] = "Reset settings",
        ["Ui.UxTab"] = "UX",
        ["Ui.MaintenanceTab"] = "Maintenance",
        ["Ui.Maintenance"] = "Maintenance / XDG paths",
        ["Ui.DesktopExperience"] = "Linux desktop experience",
        ["Ui.ViewMode"] = "View mode",
        ["Ui.QuickStartTip"] = "Linux quick-start guide and component responsibility rules",
        ["Ui.AvailableHdrModsTip"] = "Every game with a RenoDX or Luma mod in the catalogues, with a download link",
        ["Ui.AddGameTip"] = "Manually adds a Windows game executable to the persistent library",
        ["Ui.BatchDlssTip"] = "Applies or restores game-local DLSS/Streamline versions across detected games",
        ["Ui.MotdTip"] = "Shows the RHI-Linux message of the day",
        ["Ui.UpdateHistoryTip"] = "History of component installs, updates and removals",
        ["Ui.DonateTip"] = "Support the mod authors",
        ["Ui.ViewModeTip"] = "Switches between detailed and compact views",
        ["Ui.DetectedLinuxTip"] = "Detected on Linux",
        ["Ui.EditTip"] = "Separate the launcher used to start the game from the real executable that receives detection, mods and patches",
        ["Ui.RemoveManualTip"] = "Removes only the saved manual entry; game files are not changed",
        ["Ui.RepairMove"] = "Repair after move",
        ["Ui.RepairMoveTip"] = "The game moved and RHI's records or settings still point to its old folder. Checks the new folder and updates the records and settings; nothing changes if anything does not match.",

        ["Ui.DlssNote"] = "The local manager only replaces DLSS/Streamline DLLs that the game already ships. Games without DLSS use DLSS5 Feeder Full; direct swaps stay blocked while Feeder Full controls its private SR/NR stack. Apply defaults uses the saved versions; an empty default means do not change that component. The ✓ RHI applied badge means an RHI .original backup exists and restore is available.",
        ["Ui.ReShadeProfileNote"] = "Stable, Nightly, pinned Legacy and No Addons channels and a local custom DLL. RHI updates the INI and shader packages without taking ownership of external installations.",
        ["Ui.ManagedProfileControls"] = "RHI-managed profile controls",
        ["Ui.Channel"] = "Channel",
        ["Ui.LegacyVersion"] = "Legacy version",
        ["Ui.LegacyVersionNote"] = "Pinned: legacy channels are never updated automatically.",
        ["Ui.CustomDll"] = "Custom DLL",
        ["Ui.CustomDllPlaceholder"] = "Select a ReShade DLL or enter its path",
        ["Ui.D3DCompilerPlaceholder"] = "Detect in prefix or select Microsoft D3DCompiler_47.dll",
        ["Ui.D3DCompilerNote"] = "Both helpers install Microsoft's native d3dcompiler_47 and RHI applies d3dcompiler_47=native,builtin only to the selected game. Feeder Full accepts the compiler from the prefix case-insensitively and can use the selected Microsoft DLL directly; Apply still saves it to the ReShade profile. RHI never redistributes this Microsoft DLL.",
        ["Ui.FeederConsumer"] = "Feeder consumer",
        ["Ui.FeederRenoDxNote"] = "RenoDX DLSS5 is the default neural consumer for Feeder. RHI downloads the compatible version automatically; it does not install Deep Fried Chicken or add the Chicken LoadFromDllMain token.",
        ["Ui.FeederPackagePlaceholder"] = "Select the official Deep Fried Chicken package (.zip or .7z) before installing Feeder Full",
        ["Ui.FeederPackageNote"] = "Feeder Full uses RenoDX DLSS5 as the default neural consumer and keeps Deep Fried Chicken as a manual alternative. With RenoDX DLSS5, RHI downloads the dedicated consumer automatically. With DFC, the official package is validated against SHA256SUMS.txt and known AES packages use the public upstream password without persistence. Only one neural consumer may be used per installation.",
        ["Ui.QuickPackages"] = "Quick packages:",
        ["Ui.XdgSettingsNote"] = "Settings are native XDG settings and include optional add-on monitoring, detailed/compact view, tray/close-to-tray and MOTD preferences. Windows-only controls such as NVAPI and driver profiles remain outside RHI-Linux.",
        ["Ui.ManagedComponentsNote"] = "ReShade, RenoDX HDR, RenoDX Upgrade, OptiScaler, RE Framework, ReLimiter, Display Commander, Universal MFG, legacy RTX 20 FG Unlock, and per-game add-ons are available on compatible Wine/Proton targets. DirectX translation remains the runner's responsibility on Linux. External installations remain protected from removal.",
        ["Ui.ExtrasNote"] = "Extra integrations · UAL and ShortFuse orchestration.",
        ["Ui.PerGameOrchestration"] = "Per-game orchestration",
        ["Ui.LinuxPolicyNote"] = "Linux policy: DirectX translation belongs to the selected Wine/Proton runner; RHI-Linux does not install, update, replace or remove translation-layer DLLs, except DX9 HDR, which you add per game from its row. HDR only prepares a per-game launch profile. Store launchers must apply that profile; RHI does not wrap the launcher process.",
        ["Ui.UalPolicyNote"] = "Ultimate ASI Loader downloads the official build for the detected architecture, validates the GitHub-published SHA-256 when available, installs beside the selected executable, applies the per-game Wine native,builtin override and uses <name>Hooked.dll chaining only for existing DLLs not owned by RHI. Removal restores the chained original and the previous Wine override. Use ASI injection only in single-player/offline titles unless the game's anti-cheat policy explicitly allows it.",
        ["Ui.Processing"] = "Processing…",
        ["Ui.CancelOperation"] = "Cancel",
        ["Ui.LegacyWsi"] = "Legacy WSI",
        ["Ui.CopyLaunchProfile"] = "Copy launch profile",
        ["Ui.ApplyDefaults"] = "Apply defaults",
        ["Ui.RestoreAll"] = "Restore all",
        ["Ui.ApplyUpdate"] = "Apply / Update",
        ["Ui.InstallProtontricks"] = "Install via Protontricks",
        ["Ui.ManageReShadeIni"] = "Manage / merge ReShade.ini",
        ["Ui.OfficialLegacy"] = "Official legacy",
        ["Ui.FullShaderManager"] = "Full Shaders/Add-ons manager…",
        ["Ui.AutoAsiConfig"] = "⚙ Automatic ASI configuration (optional)",
        ["Ui.DragSectionTip"] = "Drag to reorder the section",
        ["Ui.CollapseExpandTip"] = "Collapse / expand",
        ["Ui.LegacyWsiTip"] = "Adds ENABLE_HDR_WSI=1 only. Keep it disabled on modern Mesa/NVIDIA unless the HDR WSI path actually requires this compatibility layer.",
        ["Ui.ApplyDefaultsTip"] = "Applies only the configured default DLSS/Streamline versions to game-local targets that already exist",
        ["Ui.RenoDxSettingsTip"] = "RenoDX compatibility settings",
        ["Ui.OptiScalerSettingsTip"] = "Advanced OptiScaler settings",
        ["Ui.Dlssg2030SettingsTip"] = "Legacy RTX 20 FG Unlock — proxy DLL",
        ["Ui.Dlssg2030Title"] = "RTX 20 FG Unlock — Legacy/Experimental",
        ["Ui.Dlssg2030Intro"] = "Legacy SM75 path for RTX 20 on Windows x64 DirectX 12 games under Wine/Proton. New RTX 30 installs moved to Universal MFG. RHI keeps this path for RTX 20 and for safe removal/update of historical managed records.",
        ["Ui.Dlssg2030RouteNote"] = "New installs expose RTX 20 / Router=SM75 only. RTX 30 / SM86 is retired in this path and now uses Universal MFG. Historical RTX 30 records remain removable/updateable so safe restore and removal are preserved.",
        ["Ui.Dlssg2030Gpu"] = "GPU generation",
        ["Ui.Dlssg2030Proxy"] = "Proxy DLL",
        ["Ui.Dlssg2030Check"] = "Check for update",
        ["Ui.Dlssg2030Update"] = "Update",
        ["Ui.Dlssg2030CompatibilityNote"] = "Experimental compatibility: confirm in a real game that the SM75 proxy loads without crashes, black screens, or device loss and that Frame Generation works. For RTX 30, use Universal MFG.",
        ["Ui.DlssEnablerStandaloneSettingsTip"] = "Standalone DLSS Enabler — proxy DLL and updates",
        ["Ui.DlssEnablerStandaloneTitle"] = "Standalone DLSS Enabler",
        ["Ui.DlssEnablerStandaloneIntro"] = "Installs DLSS Enabler as a proxy DLL directly beside the target executable, independently of OptiScaler. RHI protects occupied slots, validates the Windows x64 package, and applies native,builtin only to the selected game.",
        ["Ui.DlssEnablerStandaloneProxy"] = "Proxy DLL",
        ["Ui.DlssEnablerStandaloneCheck"] = "Check for update",
        ["Ui.DlssEnablerStandaloneUpdate"] = "Update",
        ["Ui.DlssEnablerStandaloneCompatibilityNote"] = "Experimental compatibility: confirm in a real game under Wine/Proton that the selected proxy loads without crashes, black screens, or device loss.",
        ["Ui.Rtx40MfgSettingsTip"] = "Universal MFG — GPU generation, proxy DLL and updates",
        ["Ui.RtxEncoreSettingsTip"] = "RTX Encore — file name, Neural Rendering and updates",
        ["Ui.Rtx40MfgTitle"] = "Universal MFG — Experimental",
        ["Ui.Rtx40MfgIntro"] = "Installs Universal RTXMFG as a single proxy DLL beside the target executable. RTX 40 uses DX12 plus experimental Vulkan; RTX 30 is DX12-only and remains experimental upstream. A functional Streamline DLSS Frame Generation stack is required, either game-provided or managed through OptiScaler Nightly/DLSS NR/F5 DLSSNR + Streamline/DLSS Enabler. RHI validates SHA-256/PE x64 and protects occupied slots.",
        ["Ui.Rtx40MfgGpuGeneration"] = "GPU generation",
        ["Ui.Rtx40MfgProxy"] = "Proxy DLL",
        ["Ui.Rtx40MfgCompatibility"] = "RTX 40: DirectX 12 primary plus experimental Vulkan. RTX 30: DirectX 12 only and still very experimental upstream. RTX 20 remains on the legacy SM75 path. Bink proxies stay blocked until dedicated chaining/rollback exists. Press Backspace in-game to open the RTXMFG menu.",
        ["Ui.Rtx40MfgCheck"] = "Check for update",
        ["Ui.Rtx40MfgUpdate"] = "Update",
        ["Ui.Rtx40MfgCompatibilityNote"] = "Experimental compatibility: validate on real hardware under Wine/Proton. The menu must open, Frame Generation/MFG must work, and there must be no crash, freeze, black screen, device loss, or presentation regression.",
        ["Ui.ProxyNameTip"] = "Proxy DLL name. Recommended options are version.dll, winmm.dll and winhttp.dll.",
        ["Ui.ShortFuseAutoTip"] = "When enabled: ReShade → ReShade64.asi, automatic UAL winmm→version→dinput8, HookStreamline=1 and HookDirectX=1.",
        ["Ui.ShortFuseZzzLoadOrder"] = "ZZZ load order",
        ["Ui.ShortFuseZzzTip"] = "When on, the ShortFuse add-on is deployed as zzz_renodx-dlss.addon64 so it loads last in the ReShade add-on order (Wine/Proton lists files alphabetically, as Windows does). Required for some games where other add-ons must initialise before the DLSS Tool. If ShortFuse is already installed, the file is renamed immediately.",
        ["Ui.NrVersions"] = "NR versions",
        ["Ui.LatestPinNote"] = "Latest uses the newest release; a fixed tag keeps this add-on pinned to that version for this game. Changing it while installed swaps only the add-on file.",
        ["Ui.LoadHistory"] = "Refresh versions",
        ["Ui.NrDllVersionNote"] = "nvngx_dlssnr.dll version used when installing ShortFuse or Feeder; while either is installed, changing it swaps only that DLL.",
        ["Ui.Official"] = "Official",
        ["Ui.WineProtonProxy"] = "Wine / Proton proxy",
        ["Ui.OptionStable"] = "Stable",
        ["Ui.OptionNightly"] = "Nightly",
        ["Ui.OptionLegacy"] = "Legacy",
        ["Ui.OptionCustom"] = "Custom",
        ["Ui.OptionAutomatic"] = "Automatic",
        ["Ui.OptionEnabled"] = "Enabled",
        ["Ui.OptionDisabled"] = "Disabled",
        ["Ui.OptionLatest"] = "Latest",
        ["Ui.HdrOff"] = "Off",
        ["Ui.HdrNativeWayland"] = "Native Wayland",
        ["Ui.HdrGamescope"] = "Gamescope",
        ["Ui.ProductRuntime"] = ".NET 8 / native Avalonia",
        ["Ui.Starting"] = "Starting RHI-Linux…",
        ["Ui.SelectGame"] = "Select a game",
        ["Ui.UnknownRunner"] = "unknown runner",
        ["Ui.NotCheckedYet"] = "Latest version has not been checked yet.",
        ["Ui.ReShadeApplyHint"] = "Select a channel and use Apply / Update. Stable is the default.",
        ["Ui.ReShadeGlobalDefault"] = "Select a channel and use Apply / Update. Global default: {0}.",
        ["Ui.StatusInstalled"] = "Installed",
        ["Ui.StatusExternal"] = "external",
        ["Ui.ModAuthorBy"] = "by {0}",
        ["Ui.StatusUpdateAvailable"] = "Update available",
        ["Ui.StatusUnsupported"] = "Unsupported",
        ["Ui.StatusAvailable"] = "Available",
        ["Ui.StatusUnknown"] = "Unknown",
        ["Ui.ActionInstall"] = "Install",
        ["Ui.ActionRemove"] = "Remove",
        ["Ui.CurrentHotkey"] = "Current binding: {0}",
        ["Ui.UnknownExistingKey"] = "Existing key (unrecognized)",
        ["Ui.ReShadeInternalFormat"] = "Internal ReShade format: {0}\nInternal order: key, Ctrl, Shift, Alt.",
        ["Ui.UnlocalizedPlatformMessage"] = "The component returned a technical message that is not yet translated into English; check the original text in {0}.",
        ["Ui.UnexpectedError"] = "Something went wrong: {0} RHI is still running; the details are in {1}.",
        ["Ui.UnexpectedErrorNoLog"] = "Something went wrong: {0} RHI is still running, but the details could not be written to the log.",
        ["Ui.RepeatedErrorsRestart"] = "RHI hit repeated errors and stopped showing them; the details are in {0}. Please restart RHI.",
        ["Ui.SaveFailedSessionOnly"] = "Could not save {0}: {1} The change only lasts until RHI is closed.",
        ["Ui.SaveFailedNothingChanged"] = "Could not save {0}: {1} Nothing was changed.",
        ["Ui.WhatGameLibrary"] = "the game library",
        ["Ui.WhatSettings"] = "the settings",
        ["Ui.WhatHdrProfile"] = "the HDR launch profile",
        ["Ui.WhatGamePreferences"] = "this game's preferences",
        ["Ui.WhatDeploymentRecords"] = "RHI's component records",
        ["Ui.WhatShaderPackSelection"] = "the shader pack file selection",
        ["Ui.CacheNotCleared"] = "The cache was not fully cleared: {0}",
        ["Ui.FolderNotOpened"] = "Could not open the folder: {0}"
    };


    private static readonly (string En, string Pt)[] ExactPresentationPairs =
    [
        // LinuxGameLauncher.HandoffProfileRefused (#46).
        ("Launch profile not applied: a store launcher starts this game, so the profile has to be entered in that launcher.",
         "Perfil de inicialização não aplicado: um launcher de loja inicia este jogo, então o perfil precisa ser definido nesse launcher."),
        // RenoDX compatibility settings are emitted by the platform layer with stable internal keys.
        ("Game binary directory is not known yet.", "O diretório de binários do jogo ainda não é conhecido."),
        ("No ReShade.ini was found in the game binary directory.", "Nenhum ReShade.ini foi encontrado no diretório de binários do jogo."),
        ("Run the game once with RenoDX installed to generate the [renodx] settings section.", "Execute o jogo uma vez com o RenoDX instalado para gerar a seção de configurações [renodx]."),
        ("The RHI manifest could not be loaded; built-in RenoDX compatibility settings remain available.", "O manifesto do RHI não pôde ser carregado; as configurações internas de compatibilidade do RenoDX continuam disponíveis."),
        ("RGB 10-bit + Alpha — Generic", "RGB 10-bit + Alpha — Genérico"),
        ("RGB 10-bit + Alpha — Normalized", "RGB 10-bit + Alpha — Normalizado"),
        ("RGB Float 11/11/10 — High dynamic range", "RGB Float 11/11/10 — Alta faixa dinâmica"),
        ("RGBA 16-bit — High precision", "RGBA 16-bit — Alta precisão"),
        ("RGBA 8-bit — Generic", "RGBA 8-bit — Genérico"),
        ("RGBA 8-bit — Linear", "RGBA 8-bit — Linear"),
        ("RGBA 8-bit — sRGB", "RGBA 8-bit — sRGB"),
        ("Update copy destinations", "Atualizar destinos de cópia"),
        ("Use scRGB", "Usar scRGB"),
        ("Swapchain compatibility", "Compatibilidade da swapchain"),
        ("Upgrade path", "Caminho de upgrade"),
        ("Export LUT shaders", "Exportar shaders LUT"),
        ("Disabled", "Desativado"),
        ("Enabled", "Ativado"),
        ("Output size", "Tamanho da saída"),
        ("Output ratio", "Proporção da saída"),
        ("Any size", "Qualquer tamanho"),
        ("All copies", "Todas as cópias"),
        ("HDR — Disabled", "HDR — Desativado"),
        ("SDR — Enabled", "SDR — Ativado"),

        // OptiScaler advanced UI metadata. Values/INI keys remain unchanged; only presentation is localized.
        ("OptiScaler is not RHI-managed for this game. External OptiScaler.ini files are protected from modification.", "O OptiScaler não é gerenciado pelo RHI neste jogo. Arquivos OptiScaler.ini externos são protegidos contra alterações."),
        ("Streamline / DLSS Enabler", "Streamline / DLSS Enabler"),
        ("Streamline Version", "Versão do Streamline"),
        ("FG Nvngx Replacement", "Substituição FG Nvngx"),
        ("Yes", "Sim"),
        ("No", "Não"),
        ("Auto (Default)", "Auto (Padrão)"),
        ("OptiFG (Upscaler)", "OptiFG (Upscaler)"),
        ("DLSSG via Streamline", "DLSSG via Streamline"),
        ("DLSSG via Nvngx", "DLSSG via Nvngx"),
        ("FSR 3.1 FG", "FSR 3.1 FG"),
        ("FSR 3.0 FG", "FSR 3.0 FG"),
        ("None (Real DLSSG)", "Nenhum (DLSSG real)"),
        ("Enabler", "Enabler"),
        ("FSR 3/4 FG", "FSR 3/4 FG"),
        ("Combo", "Combo"),
        ("Matches upstream Windows RHI: deploys the Streamline DLLs and the newest nvngx_dlssg.dll to OptiScaler/Streamline and dlss-enabler-headless.dll to OptiScaler/.", "Replica o RHI Windows upstream: implanta as DLLs Streamline e a nvngx_dlssg.dll mais recente em OptiScaler/Streamline e dlss-enabler-headless.dll em OptiScaler/."),
        // #135 (upstream 2.8.1): Reflex keys in the Nightly/DLSS NR FG settings.
        ("Force Reflex", "Forçar Reflex"),
        ("Use game's Reflex markers", "Usar marcadores Reflex do jogo"),
        ("Follow in-game (0)", "Seguir o jogo (0)"),
        ("Force Disable (1)", "Forçar desativado (1)"),
        ("Force Enable (2)", "Forçar ativado (2)"),
        ("[fakenvapi] ForceReflex: 0 follows the in-game Reflex setting, 1 forces Reflex off, 2 forces it on. Auto leaves it to OptiScaler. Only used when OptiScaler runs its bundled fakenvapi; Proton's own NVAPI is not changed.", "[fakenvapi] ForceReflex: 0 segue a opção Reflex do jogo, 1 força o Reflex desativado, 2 força ativado. Auto deixa a escolha ao OptiScaler. Só tem efeito quando o OptiScaler usa o fakenvapi incluído; o NVAPI do próprio Proton não é alterado."),
        ("[DLSSG] UseGamesReflexMarkers: time DLSS Frame Generation from the game's own Reflex markers. Auto is On.", "[DLSSG] UseGamesReflexMarkers: sincroniza a Geração de Quadros DLSS pelos marcadores Reflex do próprio jogo. Auto equivale a Ativado."),
        ("OptiScaler Streamline/DLSS Enabler deployment removed. The selected Streamline version preference was preserved.", "Implantação Streamline/DLSS Enabler do OptiScaler removida. A versão do Streamline escolhida foi mantida."),
        ("Version used by the OptiScaler-managed Streamline stack. Upstream Windows RHI defaults to 2.12.0 when available.", "Versão usada pela pilha Streamline gerenciada pelo OptiScaler. O RHI Windows upstream usa 2.12.0 como padrão quando disponível."),
        ("Only relevant when FG Output = DLSSG. Enabler uses dlss-enabler-headless.dll from the managed OptiScaler Streamline stack. Combo needs the FSR 3/4 FG and Enabler files: FSR FG renders the middle frames, Enabler the rest.", "Só é relevante quando a saída FG = DLSSG. Enabler usa dlss-enabler-headless.dll da pilha Streamline gerenciada pelo OptiScaler. Combo exige os arquivos de FSR 3/4 FG e do Enabler: o FSR FG gera os quadros intermediários e o Enabler os demais."),
        ("The RHI-managed OptiScaler record exists, but OptiScaler.ini is missing.", "O registro do OptiScaler gerenciado pelo RHI existe, mas o OptiScaler.ini está ausente."),
        ("LoadReshade is intentionally chain-owned by RHI Linux and is not exposed here. OptiScaler Nightly/DLSS NR/F5 DLSSNR can deploy the same Streamline + DLSS Enabler stack exposed by upstream Windows RHI.", "LoadReshade pertence intencionalmente à cadeia gerenciada pelo RHI Linux e não é exposto aqui. O OptiScaler Nightly/DLSS NR/F5 DLSSNR pode implantar a mesma pilha Streamline + DLSS Enabler oferecida pelo RHI Windows upstream."),
        ("Upscalers", "Upscalers"),
        ("Frame Generation", "Geração de quadros"),
        ("DLSS Overrides", "Overrides do DLSS"),
        ("Compatibility", "Compatibilidade"),
        ("Menu", "Menu"),
        ("DirectX 11 upscaler", "Upscaler DirectX 11"),
        ("DirectX 12 upscaler", "Upscaler DirectX 12"),
        ("Vulkan upscaler", "Upscaler Vulkan"),
        ("FG input/source", "Entrada/fonte de FG"),
        ("FG output", "Saída de FG"),
        ("HUD Fix", "Correção de HUD"),
        ("Disable HUD Fix tracking", "Desativar rastreamento da correção de HUD"),
        ("DLSS SR render preset", "Preset de renderização DLSS SR"),
        ("DLSS RR render preset", "Preset de renderização DLSS RR"),
        ("Internal render scale", "Escala interna de renderização"),
        ("Disable NVIDIA Flip Metering", "Desativar NVIDIA Flip Metering"),
        ("Framerate limit", "Limite de FPS"),
        ("OptiScaler menu shortcut", "Atalho do menu do OptiScaler"),
        ("Compatible OptiScaler values. DX11on12 options remain explicit.", "Valores compatíveis do OptiScaler. As opções DX11on12 permanecem explícitas."),
        ("Controls OptiScaler FrameGen.Enabled only; it does not install missing FG libraries.", "Controla apenas FrameGen.Enabled do OptiScaler; não instala bibliotecas de FG ausentes."),
        ("Selecting an output does not invent or download its required libraries.", "Selecionar uma saída não cria nem baixa as bibliotecas exigidas por ela."),
        ("Auto disables OptiScaler's DLSS RenderPresetOverride. Explicit values enable it and set RenderPresetForAll.", "Auto desativa o RenderPresetOverride do DLSS no OptiScaler. Valores explícitos o ativam e definem RenderPresetForAll."),
        ("Auto disables OptiScaler's DLSSD RenderPresetOverride.", "Auto desativa o RenderPresetOverride do DLSSD no OptiScaler."),
        ("This is OptiScaler's file-local UpscaleRatio override, not an NVIDIA driver-profile render-scale override.", "Este é o override UpscaleRatio local do arquivo do OptiScaler, não um override de escala de renderização do perfil do driver NVIDIA."),
        ("OptiScaler documents this primarily for Fakenvapi/NukemFG compatibility.", "O OptiScaler documenta esta opção principalmente para compatibilidade com Fakenvapi/NukemFG."),
        ("OptiScaler's Reflex-based limiter. Auto/Off leaves the limiter disabled by default.", "Limitador baseado em Reflex do OptiScaler. Auto/Desativado mantém o limitador desativado por padrão."),
        ("Auto", "Auto"),
        ("Auto / Off", "Auto / Desativado"),
        ("Auto / Insert", "Auto / Insert"),
        ("Off", "Desativado"),
        ("On", "Ativado"),
        ("No frame generation", "Sem geração de quadros"),
        ("DLSS-G input", "Entrada DLSS-G"),
        ("Nukem FSR3 input", "Entrada Nukem FSR3"),
        ("FSR FG input", "Entrada FSR FG"),
        ("Upscaler input", "Entrada do upscaler"),
        ("FSR FG 3.0 input", "Entrada FSR FG 3.0"),
        ("FSR Frame Generation", "FSR Frame Generation"),
        ("XeSS Frame Generation", "XeSS Frame Generation"),
        ("Nukem Frame Generation", "Nukem Frame Generation"),
        ("FSR 2.1 native Vulkan", "FSR 2.1 nativo Vulkan"),
        ("FSR 2.2 native Vulkan", "FSR 2.2 nativo Vulkan"),
        ("FSR 3.1 / FSR4 native Vulkan", "FSR 3.1 / FSR4 nativo Vulkan"),
        ("XeSS native Vulkan", "XeSS nativo Vulkan"),
        ("XeSS native", "XeSS nativo"),
        ("100% DLAA", "100% DLAA"),
        ("99% DLAA Alt", "99% DLAA alternativo"),
        ("88% DLAA Lite", "88% DLAA leve"),
        ("77% Ultra Quality", "77% Ultra Qualidade"),
        ("75% Quality+", "75% Qualidade+"),
        ("67% Quality", "67% Qualidade"),
        ("58% Balanced", "58% Balanceado"),
        ("50% Performance", "50% Desempenho"),
        ("45% Performance-", "45% Desempenho-"),
        ("33% Ultra Performance", "33% Ultra Desempenho"),
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // #187: OptiScaler menu keys keep the keyboard's own names in PT-BR.
        ("Delete", "Delete"), ("End", "End"), ("Page Up", "Page Up"), ("Page Down", "Page Down"),
        // w5/reshade-core rows
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.ApiSwitchLayerInstalledMessage,
            "O ReShade está instalado neste jogo como camada Vulkan. Remova o ReShade primeiro e depois mude a API gráfica."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.ApiSwitchDllInstalledMessage,
            "O ReShade está instalado neste jogo como DLL, que um jogo Vulkan não carrega. Remova o ReShade primeiro e depois defina a API gráfica como Vulkan."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.NotVulkanTargetMessage,
            "A camada Vulkan do ReShade só é oferecida para jogos Windows que usam Vulkan: apenas Vulkan encontrado no executável do jogo, ou a API gráfica definida como Vulkan nas configurações do jogo."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.PrefixNotStartedMessage,
            "O prefixo Wine/Proton do jogo ainda não foi criado. Inicie o jogo uma vez e feche-o; depois instale o ReShade."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.UnknownRunnerMessage,
            "O RHI não consegue saber qual versão do Proton ou do Wine executa este jogo, e a camada Vulkan do ReShade precisa da versão 8 ou mais nova. Escolha uma versão do Proton para o jogo no launcher, inicie o jogo uma vez e tente de novo."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.DllReShadeInstalledMessage,
            "O ReShade está instalado neste jogo como DLL. Cada jogo recebe um ReShade: remova-o primeiro e depois instale a camada Vulkan."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.NotOnRequestMessage,
            "Em um jogo Vulkan, o RHI só instala o ReShade quando você pede: instale primeiro o ReShade (camada Vulkan) pela linha dele."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.RowNote,
            "Jogo Vulkan: o ReShade é instalado como camada Vulkan só para este jogo (o carregador Vulkan da Khronos ao lado do executável). Apenas títulos single-player/offline; a injeção de add-ons pode acionar anti-cheat."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.RemovedMessage,
            "ReShade (camada Vulkan) removido: o carregador, a camada, o registro dela e a substituição do Wine foram retirados."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.DllOnlyOnVulkanGameMessage,
            "Em um jogo Vulkan, o ReShade só pode ser instalado como camada Vulkan, e este componente só funciona com o ReShade como DLL ao lado do jogo."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.Win32PrefixMessage,
            "Este jogo de 32 bits roda em um prefixo Wine só de 32 bits (um prefixo antigo do Wine ou do Lutris), onde a camada Vulkan do ReShade não carrega, então ela não é oferecida. Prefixos do Proton e prefixos novos do Wine são de 64 bits."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.IncompleteRecordMessage,
            "O registro do RHI da camada Vulkan do ReShade neste jogo está incompleto, então a atualização foi interrompida; remova o ReShade e instale-o de novo."),
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ShaderSetsUnreadableMessage,
            "As escolhas de shaders e add-ons não podem ser lidas; nada foi alterado."),
        (RHI.Platform.Linux.Settings.GlobalShaderAddonStore.LedgerUnreadableMessage,
            "Os registros de implantação não podem ser lidos, então a escolha de shaders e add-ons do jogo ainda não pode ser salva."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.ShaderSyncNotInstalledMessage,
            "O RHI não instalou o ReShade neste jogo, então ele não tem pacotes de shaders do RHI para alterar."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.ShaderSyncNoneMessage,
            "Pacotes de shaders atualizados; o próprio ReShade não foi alterado. Os pacotes de shaders do RHI foram retirados do jogo."),
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        // w7 integration rows: older English-only platform lines the wave's new paths now reach (local RenoDX add-on,
        // shaders-only sync, the OptiScaler chain removal).
        ("Game executable was not detected.", "O executável do jogo não foi detectado."),
        ("RenoDX add-ons require the Windows ReShade add-on API and are not deployed to native Linux games.",
            "Os add-ons do RenoDX precisam da API de add-ons do ReShade para Windows e não são instalados em jogos nativos do Linux."),
        ("Could not determine game bitness for the RenoDX add-on.", "Não foi possível saber se o jogo é de 32 ou 64 bits para o add-on do RenoDX."),
        ("Could not determine whether the game executable is 32-bit or 64-bit.", "Não foi possível saber se o executável do jogo é de 32 ou 64 bits."),
        ("Install ReShade with full add-on support first. RenoDX is a ReShade add-on.",
            "Instale primeiro o ReShade com suporte completo a add-ons. O RenoDX é um add-on do ReShade."),
        // w8/reshade-ini rows
        // w8/renodx rows
        // w8/commit-point rows
    ];

    private static readonly (string En, string Pt)[] PresentationPairs =
    [
        ("Winetricks is unavailable: this prefix was built by a Proton build that is no longer installed, and system Wine could damage it. Reinstall that Proton (or choose an installed one in the launcher) and refresh the library. Missing Proton: ",
         "Winetricks indisponível: este prefixo foi criado por um Proton que não está mais instalado, e o Wine do sistema poderia danificá-lo. Reinstale esse Proton (ou escolha um instalado no launcher) e atualize a biblioteca. Proton ausente: "),
        // Start-up recovery keeps a Wine override undo while the ledger cannot be read (w2-wine re-review, nit 2).
        (RHI.Platform.Linux.Deployment.WineOverrideCompensator.UnreadableLedgerPrefix,
         "O registro de implantações não pode ser lido, então esta reversão de override do Wine fica para a próxima inicialização. Cópia do registro: "),
        // The D3DCompiler helper stopped at its time limit (w2-vmsplit re-review, minor 1); tool and limit follow.
        (RHI.Platform.Linux.Deployment.D3DCompilerToolInstaller.ToolTimedOutPrefix,
         "O auxiliar do D3DCompiler demorou demais e foi interrompido; o prefixo pode ter uma instalação parcial do d3dcompiler_47, então execute a instalação novamente. Ferramenta: "),
        // A prefix whose runtime markers could not be read (w2-launch re-review, major 1); the prefix path follows.
        (RHI.Platform.Linux.Deployment.D3DCompilerToolInstaller.RuntimeUnknownMessagePrefix,
         "Winetricks indisponível: o RHI não conseguiu ler os marcadores de Wine/Proton deste prefixo (config_info), então não sabe qual Wine pode ser executado nele, e o Wine do sistema poderia danificar um prefixo do Proton. Torne o config_info do prefixo legível novamente e atualize a biblioteca. Prefixo: "),
        // ReShade setup without a host extractor (#3); the reader's reason follows the fragment.
        (RHI.Platform.Linux.Deployment.ReShadeArchiveExtractor.SetupUnreadablePrefix,
         "O instalador do ReShade não pôde ser lido, e nenhum 7z/7zz/7za ou bsdtar está instalado para tentar. Motivo: "),
        // A downloaded ZIP entry refused (size cap, length or CRC-32); the reason follows the fragment.
        (RHI.Platform.Linux.Deployment.ReShadeArchiveExtractor.ArchiveRefusedPrefix, "Arquivo baixado recusado: "),
        // RenoDX update check (#129). Mod names, HTTP status and exception text follow the fragments.
        ("RenoDX is not managed by RHI Linux.", "O RenoDX não é gerenciado pelo RHI Linux."),
        ("Game executable is unavailable; RenoDX update check skipped.", "Executável do jogo indisponível; a verificação de atualização do RenoDX foi ignorada."),
        ("Game architecture is unknown; RenoDX update check skipped.", "Arquitetura do jogo desconhecida; a verificação de atualização do RenoDX foi ignorada."),
        ("No current RenoDX catalog match was found; installed payload was preserved.", "Nenhuma correspondência atual no catálogo RenoDX foi encontrada; o arquivo instalado foi preservado."),
        ("Current RenoDX catalog match has no compatible payload for this architecture.", "A correspondência atual no catálogo RenoDX não tem arquivo compatível com esta arquitetura."),
        ("RenoDX catalog update available: ", "Atualização do catálogo RenoDX disponível: "),
        ("RenoDX update check could not reach the snapshot: ", "A verificação de atualização do RenoDX não conseguiu acessar o snapshot: "),
        ("the request timed out", "a solicitação expirou"),
        ("RenoDX snapshot was rebuilt upstream: ", "O snapshot do RenoDX foi recompilado no upstream: "),
        ("RenoDX snapshot is current: ", "O snapshot do RenoDX está atualizado: "),
        ("RenoDX update check could not compare the snapshot: the server sent no ETag, Last-Modified or size.", "A verificação de atualização do RenoDX não conseguiu comparar o snapshot: o servidor não enviou ETag, Last-Modified nem tamanho."),
        ("RenoDX update check failed: ", "A verificação de atualização do RenoDX falhou: "),
        // Download host allow-list (DEP-06, #25). The refused link follows the fragment.
        // One row per download policy, keyed on the policy's own fixed refusal prefix (the link follows it).
        (RHI.Core.Deployment.TrustedDownloadPolicy.RenoDxAddons.RefusalPrefix,
            "Download recusado: links de add-on RenoDX devem ser HTTPS em um host confiável (GitHub, GitHub Pages ou renodx.com). Link recusado: "),
        (RHI.Core.Deployment.TrustedDownloadPolicy.ReShadeAddons.RefusalPrefix,
            "Download recusado: links de add-on ReShade devem ser HTTPS em um host confiável (GitHub ou GitHub Pages). Link recusado: "),
        (RHI.Core.Deployment.TrustedDownloadPolicy.GitHub.RefusalPrefix,
            "Download recusado: links do GitHub devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Core.Deployment.TrustedDownloadPolicy.GitHubReleases.RefusalPrefix,
            "Download recusado: links de release do GitHub devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.GitHubApi.RefusalPrefix,
            "Download recusado: links da API do GitHub devem ser HTTPS em um host confiável (api.github.com). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.ReShadeSetup.RefusalPrefix,
            "Download recusado: links do instalador do ReShade devem ser HTTPS em um host confiável (reshade.me). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.ReShadeNightly.RefusalPrefix,
            "Download recusado: links da build nightly do ReShade devem ser HTTPS em um host confiável (nightly.link ou armazenamento do GitHub Actions). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.GitHubAddonRelease.RefusalPrefix,
            "Download recusado: links de release de add-on ReShade devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.OptiScaler.RefusalPrefix,
            "Download recusado: links do OptiScaler devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.Luma.RefusalPrefix,
            "Download recusado: links do Luma Framework devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.REFramework.RefusalPrefix,
            "Download recusado: links do REFramework devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.UltimateAsiLoader.RefusalPrefix,
            "Download recusado: links do Ultimate ASI Loader devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.RhiRepository.RefusalPrefix,
            "Download recusado: links de release do repositório do RHI devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.UniversalMfg.RefusalPrefix,
            "Download recusado: links do Universal MFG devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.Dlssg2030.RefusalPrefix,
            "Download recusado: links do DLSSG 20/30 unlock devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.DgVoodoo.RefusalPrefix,
            "Download recusado: links do dgVoodoo2 devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.Dlss5Feeder.RefusalPrefix,
            "Download recusado: links do DLSS5 Feeder devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.ShaderPacks.RefusalPrefix,
            "Download recusado: links de pacote de shaders do ReShade devem ser HTTPS em um host confiável (GitHub ou GitLab). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.EngineIniTemplates.RefusalPrefix,
            "Download recusado: links de modelo de Engine.ini devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        // Wave 2 trust lane (#26, #27, #37, #41): shared host-tool runner, nightly lookup, rollback that kept a copy.
        (RHI.Platform.Linux.Deployment.ExternalProcess.TimeoutPrefix, "Uma ferramenta externa demorou demais e foi interrompida: "),
        (RHI.Platform.Linux.Deployment.ExternalArchiveRunner.NoToolPrefix, "Este arquivo precisa do 7-Zip (7zz/7z/7za) ou do bsdtar, e nenhum deles está instalado. Arquivo: "),
        (RHI.Platform.Linux.Deployment.ExternalArchiveRunner.SevenZipRequiredPrefix, "Este arquivo é criptografado e só o 7-Zip (7zz/7z/7za) consegue extraí-lo; instale o 7-Zip. Arquivo: "),
        (RHI.Platform.Linux.Deployment.ExternalArchiveRunner.FailedPrefix, "A ferramenta de arquivos não conseguiu extrair o arquivo: "),
        (RHI.Platform.Linux.Deployment.ExternalArchiveRunner.SymlinkRefusal, "ele contém um link simbólico: "),
        (RHI.Platform.Linux.Deployment.ReShadeArchiveExtractor.EmptyArchiveRefusal, "o arquivo está vazio."),
        (RHI.Platform.Linux.Deployment.Dlssg2030UnlockDeploymentService.BlobMismatchPrefix, "Download recusado: o arquivo não é o blob git que o GitHub informa para este commit: "),
        (RHI.Platform.Linux.Deployment.Dlssg2030UnlockDeploymentService.BlobMissingPrefix, "O GitHub não informou um id de blob git para este arquivo: "),
        (RHI.Platform.Linux.Deployment.DeploymentMessages.PreviousFileKeptFragment, " O arquivo anterior não pôde ser recolocado; ele está guardado em: "),
        (RHI.Platform.Linux.Deployment.LumaFrameworkDeploymentService.RollbackIncompletePrefix, "A instalação do Luma Framework falhou e a reversão não foi concluída (detalhes em rhi.log): "),
        ("Universal MFG update failed: ", "Falha na atualização do Universal MFG: "),
        ("DLSS Enabler Standalone update failed: ", "Falha na atualização do DLSS Enabler Standalone: "),
        ("RTX 20 FG Unlock legado update failed: ", "Falha na atualização do RTX 20 FG Unlock legado: "),
        ("RE Framework update failed: ", "Falha na atualização do RE Framework: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.NightlyLookupFailedMessage, "Não foi possível consultar no GitHub a build nightly atual do ReShade."),
        ("RenoDX install failed: ", "Falha na instalação do RenoDX: "),
        // wikiUnlinks title kept installable by a manifest snapshot override (#141). The URL follows the fragment.
        ("Upstream wikiUnlinks removes this title from RenoDX wiki and generic matching; the upstream manifest's snapshot override is installed instead: ",
            "O wikiUnlinks do upstream remove este jogo da correspondência RenoDX pela wiki e genérica; em vez disso é instalado o snapshot indicado pelo manifesto do upstream: "),
        ("Upstream wikiUnlinks explicitly removes this title from RenoDX deployment.", "O wikiUnlinks do upstream remove explicitamente este jogo da instalação RenoDX."),
        // Remote/UI [renodx] pair that is not a plain INI entry (#122). The key follows the fragment.
        ("RenoDX setting refused: not a plain INI key and value: ", "Configuração do RenoDX recusada: chave e valor não são uma entrada INI simples: "),
        // ReShade.ini writes (review w1-renodx-p0 findings 3-6). The path (or "link -> target") follows each fragment.
        ("Read-only file not replaced (RHI never overwrites a file you locked; make it writable to let RHI change it): ",
            "Arquivo somente leitura não substituído (o RHI nunca sobrescreve um arquivo que você bloqueou; torne-o gravável para o RHI poder alterá-lo): "),
        ("Symbolic link not followed (RHI does not write through this link): ",
            "Link simbólico não seguido (o RHI não grava através deste link): "),
        ("ReShade.ini link not followed: its target must be a file named reshade.ini inside the game folder or the ReShade folder in your config directory. Link: ",
            "Link do ReShade.ini não seguido: o destino deve ser um arquivo chamado reshade.ini dentro da pasta do jogo ou da pasta ReShade no seu diretório de configuração. Link: "),
        ("Engine.ini link not followed: its target must be a file named Engine.ini inside the same Wine prefix or game project folder. Link: ",
            "Link do Engine.ini não seguido: o destino deve ser um arquivo chamado Engine.ini dentro do mesmo prefixo Wine ou da pasta do projeto do jogo. Link: "),
        ("Written content did not verify; the original file was kept: ", "O conteúdo gravado não pôde ser verificado; o arquivo original foi mantido: "),
        (" A temporary file could not be removed: ", " Um arquivo temporário não pôde ser removido: "),
        // Foreign same-name add-on (DEP-10, #29). The path follows the fragment.
        ("RenoDX install refused: a file RHI Linux does not own already has this add-on's name. Move or remove it, then install again: ",
            "Instalação do RenoDX recusada: um arquivo que o RHI Linux não gerencia já tem o nome deste add-on. Mova ou remova-o e instale novamente: "),
        ("Managed RenoDX file was modified outside RHI and will not be replaced: ", "O arquivo RenoDX gerenciado foi modificado fora do RHI e não será substituído: "),
        // ReShade during a reshade.me outage (#132, upstream 2.8.3). Fragments compose around versions, HTTP codes and causes;
        // the apply message is covered too, otherwise its cached-copy note collapses to the generic unlocalised text.
        ("ReShade source preparation failed: ", "Falha ao preparar a origem do ReShade: "),
        ("ReShade update check failed: ", "Falha ao verificar atualização do ReShade: "),
        ("reshade.me returned HTTP ", "o reshade.me respondeu HTTP "),
        (" for the ReShade setup download.", " para o download do instalador do ReShade."),
        ("reshade.me could not be reached (", "não foi possível acessar o reshade.me ("),
        ("reshade.me did not answer in time.", "o reshade.me não respondeu a tempo."),
        ("reshade.me is temporarily unavailable (HTTP ", "o reshade.me está temporariamente indisponível (HTTP "),
        (") and its page has no ReShade download link.", ") e a página dele não tem o link de download do ReShade."),
        ("reshade.me listed a ReShade download with an invalid version: ", "o reshade.me listou um download do ReShade com versão inválida: "),
        (" No cached ReShade copy is available yet; try again later.", " Ainda não há cópia do ReShade em cache; tente novamente mais tarde."),
        ("ReShade server unavailable: ", "Servidor do ReShade indisponível: "),
        (" Installed version kept (", " Versão instalada mantida ("),
        ("); nothing was changed; try again later.", "); nada foi alterado; tente novamente mais tarde."),
        ("); RHI will check again later.", "); o RHI verificará novamente mais tarde."),
        (" The cached copy (", " A cópia em cache ("),
        (") is older than the installed version (", ") é mais antiga que a versão instalada ("),
        (" reshade.me was unavailable, so the cached copy was used.", " O reshade.me estava indisponível, então a cópia em cache foi usada."),
        ("] applied as ", "] aplicado como "),
        (" (OptiScaler coexistence)", " (coexistência com OptiScaler)"),
        ("; no RHI shader packs", "; nenhum pacote de shaders do RHI"),
        ("; shader packs: ", "; pacotes de shaders: "),
        (". Existing ReShade.ini customizations are preserved.", ". As personalizações existentes do ReShade.ini foram preservadas."),
        (" Native D3DCompiler_47 is active (", " O D3DCompiler_47 nativo está ativo ("),
        (") with a per-game native,builtin override.", ") com override native,builtin por jogo."),
        (" installed with RenoDX DLSS5 ", " instalado com RenoDX DLSS5 "),
        (". No Deep Fried Chicken files or LoadFromDllMain token were installed.", ". Nenhum arquivo do Deep Fried Chicken nem token LoadFromDllMain foi instalado."),
        (" The 64-bit host ReShade came from the ReShade cache because reshade.me was unavailable.", " O ReShade do host de 64 bits veio do cache do ReShade porque o reshade.me estava indisponível."),
        // #135 OptiScaler FG stack deploy result. Fragments compose around versions and file names.
        (" deployed to OptiScaler/.", " implantados em OptiScaler/."),
        (" deployed to OptiScaler/; ", " implantados em OptiScaler/; "),
        (" was not deployed: ", " não foi implantada: "),
        ("the DLSS manifest lists no FG build.", "o manifesto DLSS não lista nenhuma versão de FG."),
        (" Existing files kept as .original and restored on removal: ", " Arquivos existentes guardados como .original e restaurados na remoção: "),
        // Control UE RR post-install (upstream 2.7.9). Fragments compose around DLL names, versions and paths.
        ("Control UE restore incomplete; RenoDX was kept:", "Restauração do Control UE incompleta; o RenoDX foi mantido:"),
        ("renderer.ini not found in the game folder; launch the game once, then reinstall RenoDX to set the HDR preset.", "renderer.ini não encontrado na pasta do jogo; abra o jogo uma vez e reinstale o RenoDX para definir o preset HDR."),
        ("m_eHDRPreset key not found; nothing changed.", "chave m_eHDRPreset não encontrada; nada foi alterado."),
        ("NVIDIA driver-profile SR preset step skipped: no driver profiles under Wine/Proton.", "Etapa do preset SR do perfil de driver NVIDIA ignorada: o Wine/Proton não tem perfis de driver."),
        ("skipped: DLSS manifest unavailable.", "ignorado: manifesto DLSS indisponível."),
        ("skipped: not in the DLSS manifest.", "ignorado: ausente do manifesto DLSS."),
        ("skipped: download failed (", "ignorado: falha no download ("),
        (" was changed outside RHI and was left as it is.", " foi alterado fora do RHI e foi mantido como está."),
        ("no longer exists; nothing to restore.", "não existe mais; nada a restaurar."),
        ("previous copy is missing (", "cópia anterior ausente ("),
        ("backup is missing; left as it is.", "cópia de segurança ausente; mantido como está."),
        ("); left as it is.", "); mantido como está."),
        ("; other settings kept.", "; demais configurações mantidas."),
        (" could not be restored: ", " não pôde ser restaurado: "),
        ("m_eHDRPreset restored to", "m_eHDRPreset restaurado para"),
        ("m_eHDRPreset already", "m_eHDRPreset já está em"),
        ("file is unexpectedly large.", "o arquivo é inesperadamente grande."),
        (" not changed: ", " não alterado: "),
        (": already ", ": já está em "),
        (": restored.", ": restaurado."),
        (": removed.", ": removido."),
        // NR in-place version swap (upstream 2.7.6). Fragments compose around versions, paths and causes.
        ("ShortFuse in-place swap failed; the previous add-on was kept:", "Falha na troca no local do ShortFuse; o add-on anterior foi mantido:"),
        ("ShortFuse in-place swap failed and the previous add-on could not be restored:", "Falha na troca no local do ShortFuse e o add-on anterior não pôde ser restaurado:"),
        ("DLSS5 Feeder in-place swap failed; the previous files were kept:", "Falha na troca no local do DLSS5 Feeder; os arquivos anteriores foram mantidos:"),
        ("DLSS5 Feeder in-place swap failed and the previous files could not be restored:", "Falha na troca no local do DLSS5 Feeder e os arquivos anteriores não puderam ser restaurados:"),
        ("nvngx_dlssnr.dll swap failed; the previous DLL was kept:", "falha na troca do nvngx_dlssnr.dll; a DLL anterior foi mantida:"),
        ("nvngx_dlssnr.dll swap failed and the previous DLL could not be restored:", "falha na troca do nvngx_dlssnr.dll e a DLL anterior não pôde ser restaurada:"),
        ("ShortFuse download failed; nothing was changed:", "Falha no download do ShortFuse; nada foi alterado:"),
        ("DLSS5 Feeder download failed; nothing was changed:", "Falha no download do DLSS5 Feeder; nada foi alterado:"),
        ("nvngx_dlssnr.dll download failed; nothing was changed:", "Falha no download do nvngx_dlssnr.dll; nada foi alterado:"),
        ("Managed file was modified outside RHI and will not be replaced:", "O arquivo gerenciado foi modificado fora do RHI e não será substituído:"),
        ("Managed file is missing:", "Arquivo gerenciado ausente:"),
        (". Reinstall the component instead.", ". Reinstale o componente."),
        ("The previous file could not be restored. Its backup is kept at", "O arquivo anterior não pôde ser restaurado. A cópia de segurança foi mantida em"),
        ("; move it back over the original name before retrying. Cause:", "; mova-a de volta para o nome original antes de tentar novamente. Causa:"),
        ("The deployment record does not own", "O registro de instalação não possui"),
        ("Swap source is missing.", "A origem da troca está ausente."),
        ("is no longer published; using", "não é mais publicado; usando"),
        ("ShortFuse add-on swapped in place to", "Add-on do ShortFuse trocado no local para"),
        (". ReShade, UAL and the DLSS stack were not changed.", ". ReShade, UAL e a pilha DLSS não foram alterados."),
        ("DLSS5 Feeder swapped in place to", "DLSS5 Feeder trocado no local para"),
        (". Consumer, DLSS DLLs, shaders and configs were not changed.", ". Consumidor, DLLs DLSS, shaders e configurações não foram alterados."),
        ("nvngx_dlssnr.dll swapped in place to", "nvngx_dlssnr.dll trocado no local para"),
        (". Nothing else was changed.", ". Nada mais foi alterado."),
        ("nvngx_dlssnr.dll already matches", "nvngx_dlssnr.dll já corresponde a"),
        ("is already installed.", "já está instalado."),
        ("is not managed by RHI Linux.", "não é gerenciado pelo RHI Linux."),
        ("does not own nvngx_dlssnr.dll for this game; nothing was swapped.", "não possui nvngx_dlssnr.dll para este jogo; nada foi trocado."),
        ("The DLSS manifest does not provide a Neural Rendering runtime.", "O manifesto DLSS não fornece um runtime de Renderização Neural."),
        ("The ShortFuse record does not own a ShortFuse add-on. Remove and reinstall ShortFuse.", "O registro do ShortFuse não possui um add-on ShortFuse. Remova e reinstale o ShortFuse."),
        ("The DLSS5 Feeder record does not own a Feeder add-on. Remove and reinstall DLSS5 Feeder.", "O registro do DLSS5 Feeder não possui um add-on Feeder. Remova e reinstale o DLSS5 Feeder."),
        ("Legacy DLSS5 Feeder add-on-only deployment cannot be swapped in place. Remove it, then install the full Feeder stack.", "A instalação legada do DLSS5 Feeder (somente add-on) não pode ser trocada no local. Remova-a e instale a pilha completa do Feeder."),
        ("Legacy DLSS5 Feeder add-on-only deployment; remove and reinstall to update.", "Instalação legada do DLSS5 Feeder (somente add-on); remova e reinstale para atualizar."),
        ("No ShortFuse or DLSS5 Feeder Full stack owns nvngx_dlssnr.dll for this game; the version applies at the next install.", "Nenhuma pilha ShortFuse ou DLSS5 Feeder Full possui nvngx_dlssnr.dll neste jogo; a versão vale para a próxima instalação."),
        ("DLSS5 Feeder update check failed:", "Falha ao verificar atualização do DLSS5 Feeder:"),
        ("This game was moved after RHI installed components: their records still point to ", "Este jogo mudou de pasta depois que o RHI instalou componentes: os registros ainda apontam para "),
        (". Nothing was changed. Use Repair after move first.", ". Nada foi alterado. Use Reparar após mover primeiro."),
        ("Nothing to repair: RHI's records for this game already match its folder.", "Nada a reparar: os registros do RHI para este jogo já correspondem à pasta dele."),
        ("Repair after move cannot tell where this folder went: ", "Reparar após mover não consegue saber para onde esta pasta foi: "),
        ("Nothing was changed: the old folder still holds the files RHI installed, so RHI cannot tell which copy the game uses: ", "Nada foi alterado: a pasta antiga ainda contém os arquivos instalados pelo RHI, então o RHI não sabe qual cópia o jogo usa: "),
        (". If that copy is no longer needed, delete it; to keep it as a backup, rename or move it. Then choose Repair after move again.", ". Se essa cópia não for mais necessária, apague-a; para guardá-la como backup, renomeie-a ou mova-a. Depois escolha Reparar após mover de novo."),
        ("Nothing was changed: RHI records from more than one earlier folder match this game, so it cannot tell which are its own. Install the components again instead. Folders: ", "Nada foi alterado: registros do RHI de mais de uma pasta anterior correspondem a este jogo, então não é possível saber quais são dele. Instale os componentes de novo. Pastas: "),
        ("Nothing was changed: the game's records and settings could not all be updated. ", "Nada foi alterado: não foi possível atualizar todos os registros e configurações do jogo. "),
        ("Repair after move failed part-way: ", "Reparar após mover falhou no meio: "),
        (" These files were already rewritten and could not be put back: ", " Estes arquivos já tinham sido regravados e não puderam ser restaurados: "),
        (" changed while the change was being prepared. Nothing was changed; try again.", " mudou enquanto a alteração era preparada. Nada foi alterado; tente de novo."),
        (" changed while the change was being written.", " mudou enquanto a alteração era gravada."),
        (" changed while it was being read. Nothing was changed; try again.", " mudou enquanto era lido. Nada foi alterado; tente de novo."),
        ("Nothing was changed: one file was staged twice by two stores.", "Nada foi alterado: o mesmo arquivo foi preparado duas vezes."),
        ("The update history cannot be read: ", "O histórico de atualizações não pode ser lido: "),
        (". Clear the history (a copy of it is kept), then try again.", ". Limpe o histórico (uma cópia é mantida) e tente de novo."),
        ("More than one record for the same component; remove and reinstall it instead: ", "Há mais de um registro para o mesmo componente; remova e reinstale-o: "),
        ("Nothing was changed: a file RHI installed is not at the new location: ", "Nada foi alterado: um arquivo instalado pelo RHI não está no novo local: "),
        ("Nothing was changed: a file RHI installed differs at the new location: ", "Nada foi alterado: um arquivo instalado pelo RHI está diferente no novo local: "),
        (RHI.Platform.Linux.Deployment.DeploymentRelocation.RepairMissingAfterMovePrefix, "Nada foi alterado: a pasta ou o arquivo indicado nas configurações do jogo não está no novo local: "),
        (RHI.Platform.Linux.Deployment.DeploymentRelocation.RepairSettingsDone, "As configurações do jogo agora apontam para a nova pasta."),
        (" cannot be read and could not be copied aside. RHI will not overwrite it; fix or delete the file.", " não pode ser lido e não pôde ser copiado. O RHI não vai sobrescrevê-lo; corrija ou apague o arquivo."),
        (" cannot be read; a copy was kept at ", " não pode ser lido; uma cópia foi guardada em "),
        (". RHI will not overwrite it; fix or delete the file.", ". O RHI não vai sobrescrevê-lo; corrija ou apague o arquivo."),
        ("Luma mode enabled", "Modo Luma ativado"),
        ("Luma mode not enabled", "Modo Luma não ativado"),
        ("authority: user enabled", "autoridade: ativado pelo usuário"),
        ("authority: user disabled", "autoridade: desativado pelo usuário"),
        ("authority: upstream lumaDefaultGames", "autoridade: lumaDefaultGames do upstream"),
        ("authority: automatic/no default", "autoridade: automático/sem padrão"),
        ("authority: automatic/not primed", "autoridade: automático/ainda não carregado"),
        ("RenoDX coexistence allowed by lumaRenodxCompat", "coexistência com RenoDX permitida por lumaRenodxCompat"),
        ("RenoDX/Luma mutually exclusive", "RenoDX/Luma mutuamente exclusivos"),
        ("manifest policy not primed", "dados de compatibilidade ainda não carregados"),
        ("Native Linux target: the current RHI Linux integration deploys Windows DLL components only to Wine/Proton games.", "Alvo Linux nativo: a integração atual do RHI Linux instala componentes DLL do Windows apenas em jogos Wine/Proton."),
        ("Safe game-local ReShade deployment is currently enabled for DirectX 8/9/10/11/12 and OpenGL targets; this API needs a different integration path.", "A instalação segura e local do ReShade está habilitada para alvos DirectX 8/9/10/11/12 e OpenGL; esta API exige outro caminho de integração."),
        ("Start the Windows game once so its Wine/Proton prefix is created before installing ReShade.", "Execute o jogo Windows uma vez para criar o prefixo Wine/Proton antes de instalar o ReShade."),
        ("Will coexist with RHI-managed OptiScaler as ReShade64.dll; RHI enables OptiScaler's LoadReshade setting automatically.", "Vai coexistir com o OptiScaler gerenciado pelo RHI como ReShade64.dll; o RHI ativa LoadReshade no OptiScaler automaticamente."),
        ("Full add-on ReShade build. Use only with single-player/offline titles; add-on injection can trigger anti-cheat.", "Build do ReShade com suporte completo a add-ons. Use apenas em jogos single-player/offline; a injeção de add-ons pode acionar anti-cheat."),
        ("Start the RE Engine game once so its Wine/Proton prefix exists before installing RE Framework.", "Execute o jogo RE Engine uma vez para criar o prefixo Wine/Proton antes de instalar o RE Framework."),
        ("An external dinput8.dll already exists beside the game executable. RHI will not overwrite or adopt that proxy.", "Já existe um dinput8.dll externo ao lado do executável. O RHI não sobrescreverá nem adotará esse proxy."),
        ("Remove RHI-managed ReShade first; this RE Engine ReShade path depends on RE Framework.", "Remova primeiro o ReShade gerenciado pelo RHI; este caminho de ReShade para RE Engine depende do RE Framework."),
        ("Upstream wikiUnlinks explicitly removes this title from RenoDX named and generic deployment.", "O wikiUnlinks do upstream remove explicitamente este jogo das instalações RenoDX nomeadas e genéricas."),
        ("Remove RHI-managed Luma Framework before installing RenoDX HDR; this title is not listed in upstream lumaRenodxCompat.", "Remova o Luma Framework gerenciado pelo RHI antes de instalar RenoDX HDR; este jogo não está em lumaRenodxCompat do upstream."),
        ("Upstream lumaRenodxCompat allows RenoDX HDR and Luma Framework to coexist for this title. Existing ownership and dependency guards still apply.", "O lumaRenodxCompat do upstream permite coexistência entre RenoDX HDR e Luma Framework neste jogo. As proteções de ownership e dependências continuam válidas."),
        ("RenoDX Upgrade requires a detected 32-bit or 64-bit Windows executable.", "RenoDX Upgrade exige um executável Windows de 32 ou 64 bits detectado."),
        ("ReLimiter and Display Commander are mutually exclusive in upstream RHI. Remove Display Commander first.", "ReLimiter e Display Commander são mutuamente exclusivos no RHI upstream. Remova o Display Commander primeiro."),
        ("Display Commander and ReLimiter are mutually exclusive in upstream RHI. Remove ReLimiter first.", "Display Commander e ReLimiter são mutuamente exclusivos no RHI upstream. Remova o ReLimiter primeiro."),
        ("MFG Ada Unlock is a 64-bit ReShade add-on.", "MFG Ada Unlock é um add-on ReShade de 64 bits."),
        ("This game already has native DLSS. Feeder Full is the no-DLSS path; use the native DLSS/Neural Rendering workflow instead.", "Este jogo já possui DLSS nativo. O Feeder Full é o caminho para jogos sem DLSS; use o fluxo de DLSS nativo/Renderização Neural."),
        ("ShortFuse auto-setup will not rewrite an active OptiScaler/ReShade chain. Remove OptiScaler first.", "A configuração automática do ShortFuse não reescreve uma cadeia OptiScaler/ReShade ativa. Remova o OptiScaler primeiro."),
        ("ShortFuse upstream supports 64-bit Windows targets except OpenGL; this game is detected as OpenGL.", "O ShortFuse upstream suporta alvos Windows de 64 bits exceto OpenGL; este jogo foi detectado como OpenGL."),
        ("ShortFuse is enabled for detected 64-bit Windows games.", "ShortFuse está habilitado para jogos Windows de 64 bits detectados."),
        ("Start the game once so its Wine/Proton prefix exists before installing ShortFuse.", "Execute o jogo uma vez para criar o prefixo Wine/Proton antes de instalar o ShortFuse."),
        ("Remove ShortFuse before changing the OptiScaler/ReShade proxy chain.", "Remova o ShortFuse antes de alterar a cadeia de proxy OptiScaler/ReShade."),
        ("Automatic OptiScaler deployment is currently limited to detected 64-bit Windows executables.", "A instalação automática do OptiScaler está limitada a executáveis Windows de 64 bits detectados."),
        ("Start the game once so its Wine/Proton prefix exists before installing OptiScaler.", "Execute o jogo uma vez para criar o prefixo Wine/Proton antes de instalar o OptiScaler."),
        ("Managed by RHI Linux", "Gerenciado pelo RHI Linux"),
        ("RE Engine games require RE Framework. When installing ReShade, RHI will automatically install RE Framework first and continue only if the dependency becomes valid.", "Jogos RE Engine exigem RE Framework. Ao instalar ReShade, o RHI instalará automaticamente o RE Framework primeiro e só continuará se a dependência ficar válida."),
        ("Luma Framework requires RHI-managed ReShade. An external ReShade was detected and will be preserved; RHI will not automatically adopt or overwrite it.", "Luma Framework exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; o RHI não o adotará nem sobrescreverá automaticamente."),
        ("Luma Framework requires RHI-managed ReShade; installing Luma will automatically install ReShade before the Luma package. On DX9, dgVoodoo2 is still prepared automatically when required by the catalog.", "Luma Framework exige ReShade gerenciado pelo RHI; a instalação do Luma instalará ReShade automaticamente antes do pacote Luma. Em DX9, dgVoodoo2 continua sendo preparado automaticamente quando exigido pelo catálogo."),
        ("RenoDX HDR requires ReShade with full add-on support; RHI will automatically install ReShade before RenoDX.", "RenoDX HDR exige ReShade com suporte completo a add-ons; o RHI instalará ReShade automaticamente antes do RenoDX."),
        ("RenoDX Upgrade is a ReShade add-on; RHI will automatically install ReShade before the Upgrade.", "RenoDX Upgrade é um add-on do ReShade; o RHI instalará ReShade automaticamente antes do Upgrade."),
        ("is a ReShade add-on; RHI will automatically install ReShade before this component.", "é um add-on do ReShade; o RHI instalará ReShade automaticamente antes deste componente."),
        ("ShortFuse requires RHI-managed ReShade. An external ReShade was detected and will be preserved; RHI will not automatically adopt it.", "ShortFuse exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; o RHI não o adotará automaticamente."),
        ("ShortFuse requires RHI-managed ReShade; ReShade will be installed automatically before ShortFuse. If Auto Config is enabled, Ultimate ASI Loader is still installed automatically by the ShortFuse flow.", "ShortFuse exige ReShade gerenciado pelo RHI; o ReShade será instalado automaticamente antes do ShortFuse. Se Auto Config estiver ativo, o Ultimate ASI Loader continua sendo instalado automaticamente pelo próprio fluxo do ShortFuse."),
        // ShortFuse ZZZ load-order failures (fragments of ShortFuseDeploymentService messages; paths and
        // exception text sit between them).
        ("The RHI deployment ledger is unreadable, so the ShortFuse load-order change was refused and nothing was renamed. Quarantined copy:", "O registro de implantações do RHI está ilegível; a mudança da ordem de carga do ShortFuse foi recusada e nada foi renomeado. Cópia em quarentena:"),
        ("The ShortFuse ownership record has no add-on entry; the load-order rename is blocked.", "O registro de propriedade do ShortFuse não tem entrada do add-on; a renomeação da ordem de carga está bloqueada."),
        (" already exists and is not owned by this ShortFuse record; the load-order rename is blocked.", " já existe e não pertence a este registro do ShortFuse; a renomeação da ordem de carga está bloqueada."),
        ("Could not rename the ShortFuse add-on to ", "Não foi possível renomear o add-on do ShortFuse para "),
        ("Could not save the ShortFuse ownership record, so the add-on was renamed back to ", "Não foi possível salvar o registro de propriedade do ShortFuse; por isso o add-on voltou a se chamar "),
        ("The ShortFuse add-on is now named ", "O add-on do ShortFuse agora se chama "),
        (", but its ownership record still says ", ", mas o registro de propriedade ainda indica "),
        (": saving the record failed (", ": a gravação do registro falhou ("),
        (") and renaming the add-on back also failed (", ") e renomear o add-on de volta também falhou ("),
        ("). Rename the file back by hand, or switch the toggle again once the ledger is writable.", "). Renomeie o arquivo de volta manualmente ou alterne a opção de novo quando o registro puder ser gravado."),
        ("The ShortFuse add-on is already named ", "O add-on do ShortFuse já se chama "),
        (", but saving the repaired ownership record failed (", ", mas a gravação do registro de propriedade corrigido falhou ("),
        ("); the record still says ", "); o registro ainda indica "),
        ("The ShortFuse add-on is missing from ", "O add-on do ShortFuse está ausente de "),
        ("Both renodx-dlss.addon64 and zzz_renodx-dlss.addon64 exist in ", "renodx-dlss.addon64 e zzz_renodx-dlss.addon64 existem ambos em "),
        ("; ReShade would load ShortFuse twice. Remove the one RHI does not own first.", "; o ReShade carregaria o ShortFuse duas vezes. Remova primeiro o arquivo que não pertence ao RHI."),
        ("ShortFuse ownership record has no SHA-256 for ", "O registro de propriedade do ShortFuse não tem SHA-256 para "),
        ("ShortFuse-owned file was modified outside RHI: ", "Arquivo pertencente ao ShortFuse foi modificado fora do RHI: "),
        ("Could not save the repaired ShortFuse ownership record (", "Não foi possível salvar o registro de propriedade corrigido do ShortFuse ("),
        ("); Remove is blocked before anything was changed.", "); a remoção foi bloqueada antes de qualquer alteração."),
        // Wave-1 core/platform messages (integration w1 review, ui-build finding 1). Interpolated values sit after the
        // fixed fragments; the fragments are matched on the producers' own constants where they exist.
        (RHI.Core.Deployment.AtomicFile.NoFolderPrefix, "Não foi possível determinar a pasta deste caminho: "),
        (RHI.Core.Deployment.AtomicFile.SizeLimitPrefix, "O conteúdo excede o limite de tamanho ("),
        (RHI.Core.Deployment.AtomicFile.CopyVerifyFailurePrefix, "A verificação da cópia falhou (o arquivo gravado não corresponde ao que foi lido): "),
        (RHI.Core.Deployment.AtomicFile.ShaMismatchPrefix, "SHA-256 divergente (esperado "),
        (RHI.Core.Deployment.AtomicFile.ShaMismatchMiddle, ", real "),
        (RHI.Core.Deployment.AtomicFile.SameFilePrefix, "Origem e destino são o mesmo arquivo: "),
        (RHI.Core.Deployment.AtomicFile.MoveTargetExistsPrefix, "Movimentação recusada, o destino já existe: "),
        (RHI.Core.Deployment.AtomicFile.MoveFailedPrefix, "Não foi possível mover o arquivo (errno "),
        (RHI.Core.Deployment.TrustedDownload.SizeLimitPrefix, "O download é maior que o limite de tamanho ("),
        ("Could not register this RHI instance in ", "Não foi possível registrar esta instância do RHI em "),
        ("Journal entry recovery failed: ", "A recuperação da entrada do diário falhou: "),
        ("Unreadable journal entry kept at ", "Entrada ilegível do diário mantida em "),
        ("No handler is registered for this step; it was left for the next start: ", "Nenhum tratador está registrado para esta etapa; ela ficou para a próxima inicialização: "),
        ("Changed after the interruption and left as it is: ", "Alterado depois da interrupção e mantido como está: "),
        ("Undo deferred to the next start: ", "Desfazer adiado para a próxima inicialização: "),
        ("Could not undo this step: ", "Não foi possível desfazer esta etapa: "),
        ("The journal entry could not be narrowed to the deferred steps (", "A entrada do diário não pôde ser reduzida às etapas adiadas ("),
        ("The journal entry could not be finalised; it is cleaned on the next start: ", "A entrada do diário não pôde ser finalizada; ela será limpa na próxima inicialização: "),
        ("Rolled back, but the journal entry could not be removed: ", "Desfeito, mas a entrada do diário não pôde ser removida: "),
        ("Could not remove ", "Não foi possível remover "),
        (": not put back; its content was last placed in ", ": não foi devolvido; o conteúdo dele foi colocado por último em "),
        (", which was changed after the interruption.", ", que foi alterado depois da interrupção."),
        (": no surviving copy with the pre-operation content (", ": nenhuma cópia restante com o conteúdo anterior à operação ("),
        (") was found; every copy was kept.", ") foi encontrada; todas as cópias foram mantidas."),
        (": the backup disappeared although this operation never changed it; every copy was kept.", ": a cópia de segurança sumiu, embora esta operação nunca a tenha alterado; todas as cópias foram mantidas."),
        (": removed by something else after the interruption (this operation never changed it); nothing to put back.", ": removido por outro processo depois da interrupção (esta operação nunca o alterou); nada a devolver."),
        (": changed after the interruption (neither its earlier content nor what RHI wrote); left as it is.", ": alterado depois da interrupção (nem o conteúdo anterior nem o que o RHI gravou); mantido como está."),
        (" It did not exist before the operation.", " Ele não existia antes da operação."),
        (": changed after the interruption; left as it is.", ": alterado depois da interrupção; mantido como está."),
        (": rollback failed (", ": a reversão falhou ("),
        ("); every copy was kept.", "); todas as cópias foram mantidas."),
        ("RHI's copy of its earlier content was moved to ", "A cópia do RHI com o conteúdo anterior foi movida para "),
        ("Wine is running in this prefix and would overwrite the change when it exits, so this Wine override is undone at the next start: ",
            "O Wine está em execução neste prefixo e sobrescreveria a alteração ao sair; por isso este override do Wine será desfeito na próxima inicialização: "),
        ("This Wine override was changed after the interruption and was left as it is: ", "Este override do Wine foi alterado depois da interrupção e foi mantido como está: "),
        // Wine registry editor (#24, #30). The prefix path, or "dll = 'value'", follows each fragment.
        (RHI.Platform.Linux.Deployment.WineRegistryEditor.PrefixInUseRefusalPrefix,
            "O Wine está em execução neste prefixo e regrava o registro dele ao parar, por isso o RHI não alterou nada. Feche o jogo e tudo o que usa este prefixo, aguarde alguns segundos e tente de novo. Prefixo: "),
        (RHI.Platform.Linux.Deployment.WineRegistryEditor.BusyRefusalPrefix,
            "Outra tarefa do RHI está alterando o registro Wine deste prefixo, por isso o RHI não alterou nada. Tente de novo em instantes. Prefixo: "),
        (RHI.Platform.Linux.Deployment.WineRegistryEditor.MissingHiveMessage,
            "O user.reg do Wine/Proton não foi encontrado. Inicie o jogo uma vez para que o prefixo seja inicializado."),
        (RHI.Platform.Linux.Deployment.WineDllOverrideService.KeptUserValueNotePrefix,
            " O RHI manteve este override do Wine como está, porque ele foi alterado depois que o RHI o definiu: "),
        (RHI.Platform.Linux.Deployment.D3DCompilerToolInstaller.OverrideNotSetWineRunningPrefix,
            "O D3DCompiler_47 nativo foi instalado, mas o Wine ainda está em execução neste prefixo, por isso o RHI não definiu o override d3dcompiler_47. Feche tudo o que usa este prefixo e execute a instalação de novo. Prefixo: "),
        ("Source file does not exist: ", "O arquivo de origem não existe: "),
        ("Target directory does not exist: ", "O diretório de destino não existe: "),
        ("No .original backup exists for ", "Não existe cópia .original para "),
        ("The deployment journal could not be written (", "O diário de implantação não pôde ser gravado ("),
        ("the deployment journal could not be written (", "o diário de implantação não pôde ser gravado ("),
        ("); nothing was changed.", "); nada foi alterado."),
        ("). Nothing was changed.", "). Nada foi alterado."),
        (" Rollback was incomplete (", " A reversão ficou incompleta ("),
        ("); the pre-change copies (*", "); as cópias anteriores à alteração (*"),
        (") were kept next to the affected files.", ") foram mantidas ao lado dos arquivos afetados."),
        ("ReShade install/update failed: ", "Falha na instalação/atualização do ReShade: "),
        ("DLSS manifest did not answer and no cached copy exists (timeout ", "O manifesto de DLSS não respondeu e não há cópia em cache (tempo limite "),
        ("Downloaded archive does not contain ", "O arquivo baixado não contém "),
        ("The cached DLL is not readable after extraction: ", "A DLL em cache não pode ser lida após a extração: "),
        ("Extracted file is unreadable or unexpectedly small: ", "O arquivo extraído está ilegível ou é pequeno demais: "),
        ("Manifest entry has a malformed sha256: ", "Entrada do manifesto com sha256 malformado: "),
        ("DLSS manifest has no entries.", "O manifesto de DLSS não tem entradas."),
        ("Manifest entry has no version.", "Entrada do manifesto sem versão."),
        (" Rollback incomplete: ", " Reversão incompleta: "),
        (" Restore incomplete: ", " Restauração incompleta: "),
        (" Control UE changes could not be undone: ", " As alterações do Control UE não puderam ser desfeitas: "),
        ("Could not restore ", "Não foi possível restaurar "),
        ("; its previous contents were kept at ", "; o conteúdo anterior foi mantido em "),
        (" nor keep a copy of its previous contents (", " nem guardar uma cópia do conteúdo anterior ("),
        ("Downloaded RenoDX file is not a valid PE add-on (possible HTML/error response).", "O arquivo RenoDX baixado não é um add-on PE válido (possível resposta HTML/de erro)."),
        ("RenoDX removal failed: ", "Falha na remoção do RenoDX: "),
        ("Downloaded RenoDX Upgrade add-on is not a valid PE payload or is unexpectedly small.", "O add-on RenoDX Upgrade baixado não é um arquivo PE válido ou é pequeno demais."),
        ("Downloaded RenoDX Upgrade payload has the wrong architecture (expected ", "O arquivo RenoDX Upgrade baixado tem a arquitetura errada (esperado "),
        ("RenoDX Upgrade install failed: ", "Falha na instalação do RenoDX Upgrade: "),
        ("Downloaded Universal UE DOF Fix is not a valid PE payload or is unexpectedly small.", "O Universal UE DOF Fix baixado não é um arquivo PE válido ou é pequeno demais."),
        ("Downloaded Universal UE DOF Fix failed the SHA-256 digest published by GitHub.", "O Universal UE DOF Fix baixado não confere com o SHA-256 publicado pelo GitHub."),
        ("DOF Fix update check failed: ", "Falha ao verificar atualização do DOF Fix: "),
        ("Upstream RHI manifest unavailable: ", "Manifesto do RHI upstream indisponível: "),
        ("No safe Winetricks route is available for this prefix.", "Nenhuma rota segura do Winetricks está disponível para este prefixo."),
        (RHI.Platform.Linux.Deployment.DeploymentRelocation.RepairDonePrefix, "Registros atualizados para a nova pasta do jogo: "),
        // A staged multi-file change rolled back completely (the reason follows), and its staged-twice reason alone
        // (Repair after move drops the lead-in, its own message already says nothing changed).
        (RHI.Platform.Linux.Persistence.StagedFileCommit.NothingChangedPrefix, "Nada foi alterado: "),
        (RHI.Platform.Linux.Persistence.StagedFileCommit.StagedTwiceReason, "o mesmo arquivo foi preparado duas vezes."),
        // Desktop opener (folders and links); the path or link follows the fragment.
        (RHI.Platform.Linux.Paths.LinuxDesktopOpener.DirectoryMissingPrefix, "A pasta não existe: "),
        (RHI.Platform.Linux.Paths.LinuxDesktopOpener.UnsupportedLinkPrefix, "Link não suportado: "),
        (RHI.Platform.Linux.Paths.LinuxDesktopOpener.NoOpenerMessage, "Nenhum programa para abrir pastas ou links (xdg-open, gio, kde-open) pôde ser iniciado."),
        // .NET's own file-system errors behind a refused save or folder creation (path between the fragments).
        ("Access to the path '", "Acesso ao caminho '"),
        ("' is denied.", "' negado."),
        ("Could not find a part of the path '", "Parte do caminho não foi encontrada: '"),
        ("Could not find file '", "Arquivo não encontrado: '"),
        ("No space left on device : '", "Sem espaço no dispositivo: '"),
        ("Read-only file system : '", "Sistema de arquivos somente leitura: '"),
        ("no ZIP archive found in the setup", "nenhum arquivo ZIP encontrado no instalador"),
        ("the archive holds no ReShade64.dll or ReShade32.dll", "o arquivo não contém ReShade64.dll nem ReShade32.dll"),
        (": declared size over the limit (", ": tamanho declarado acima do limite ("),
        ("the ReShade DLLs together declare more than the limit (", "as DLLs do ReShade juntas declaram mais que o limite ("),
        ("holds more bytes than it declares (", "contém mais bytes do que declara ("),
        ("holds a different size than it declares (", "tem tamanho diferente do que declara ("),
        ("CRC-32 does not match the archive (corrupt download)", "o CRC-32 não confere com o arquivo (download corrompido)"),
        ("No specific RenoDX profile was found in the upstream catalog and this game does not match the safe generic Unreal/Unity fallbacks. Automatic installation was disabled to avoid applying an incorrect add-on.", "Nenhum perfil RenoDX específico foi encontrado no catálogo upstream e este jogo não corresponde aos fallbacks genéricos seguros de Unreal/Unity. A instalação automática foi desativada para evitar aplicar um add-on incorreto."),
        // w3/sync rows
        (RHI.Platform.Linux.Deployment.DownloadSources.OptiScalerF5.RefusalPrefix,
            "Download recusado: links do OptiScaler F5 DLSSNR devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RHI.Platform.Linux.Deployment.NeuralRenderingRuntimeOwnership.OwnedByPrefix, "nvngx_dlssnr.dll já pertence a outro componente do RHI neste jogo; remova-o primeiro: "),
        (RHI.Platform.Linux.Deployment.OptiScalerPackageProvider.ArchivePathRefusedPrefix, "O arquivo do OptiScaler contém um caminho que o RHI não extrai: "),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.FeederBlocksNrVariantsNote, " DLSS NR e F5 DLSSNR ficam bloqueados enquanto o DLSS5 Feeder é gerenciado, porque os dois seriam donos de nvngx_dlssnr.dll; Stable/Nightly continuam disponíveis."),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.NightlyPinnedCurrentPrefix, "O OptiScaler Nightly está fixado e atualizado: "),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.NightlyPinnedNotUpdatedPrefix, "O OptiScaler Nightly está fixado, então não foi atualizado: "),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.NightlyPinWrongChannelMessage, "Uma build nightly só pode ser fixada no canal Nightly do OptiScaler."),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.NightlyBuildInvalidPrefix, "Não é uma data de build nightly do OptiScaler: "),
        (RHI.Platform.Linux.Deployment.OptiScalerPackageProvider.NightlyBuildMismatchPrefix, "O GitHub devolveu outra build nightly do OptiScaler que não a fixada: "),
        // w3/journal rows
        // A journaled removal could not mark its record before the first change (record-removed commit probe).
        (RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.RemovalUnmarkedPrefix,
         "O registro de implantações não pôde ser atualizado antes da remoção ("),
        // RE Framework ⚙ "Delete _storage_ folder" (#159); a path follows each.
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StorageNotFoundPrefix, "Nenhuma pasta _storage_ foi encontrada na pasta do jogo: "),
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StorageLinkRefusedPrefix, "A pasta _storage_ é um link para outro lugar, então o RHI não a apagou: "),
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StorageOutsideGameRefusedPrefix, "A pasta _storage_ não está dentro da pasta do jogo, então o RHI não a apagou: "),
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StorageWineRunningPrefix, "O jogo está em execução no prefixo Wine; feche-o antes de apagar a pasta _storage_: "),
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StorageDeletedPrefix, "Pasta _storage_ apagada (o RE Framework a refaz na próxima abertura): "),
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StoragePartlyDeletedPrefix, "A pasta _storage_ não pôde ser apagada por completo; o que restou está listado no rhi.log: "),
        (RHI.Platform.Linux.Deployment.REFrameworkDeploymentService.StorageUnreadablePrefix, "A pasta _storage_ não pôde ser lida, então o RHI não apagou nada (o motivo está no rhi.log): "),
        ("RE Framework removal failed and was rolled back: ", "A remoção do RE Framework falhou e foi desfeita: "),
        ("RE Framework update rolled back: ", "A atualização do RE Framework foi desfeita: "),
        // ShortFuse removed; the ASI Loader it installed could not be (it is removed last, as its own operation).
        (RHI.Platform.Linux.Deployment.ShortFuseDeploymentService.UalKeptPrefix, "O Ultimate ASI Loader instalado pelo ShortFuse continua instalado (remova-o separadamente): "),
        // Review w3-journal #4/#8: removal, install and update failures of the journaled services and ShortFuse's success
        // lines (the one without "UAL state" when the ASI Loader stays installed). The name comes first, so the generic
        // tails read "<component>: …".
        (RHI.Platform.Linux.Deployment.DofFixDeploymentService.RemovalRolledBackPrefix, "A remoção do DOF Fix falhou e foi desfeita: "),
        (RHI.Platform.Linux.Deployment.RenoDxUpgradeDeploymentService.RemovalRolledBackPrefix, "A remoção do RenoDX Upgrade falhou e foi desfeita: "),
        (RHI.Platform.Linux.Deployment.ShortFuseDeploymentService.RemovalFailedPrefix,
         "A remoção do ShortFuse falhou; o estado instalado de ShortFuse/ReShade/INI/registro foi restaurado a partir das cópias da transação onde possível: "),
        (RHI.Platform.Linux.Deployment.ShortFuseDeploymentService.RemovedMessage,
         "ShortFuse removido. O RHI restaurou os arquivos DLSS/Streamline anteriores, o nome do ReShade, os valores do reshade.ini, o estado do UAL e a posse do override Wine do ReShade."),
        (RHI.Platform.Linux.Deployment.ShortFuseDeploymentService.RemovedUalKeptMessage,
         "ShortFuse removido. O RHI restaurou os arquivos DLSS/Streamline anteriores, o nome do ReShade, os valores do reshade.ini e a posse do override Wine do ReShade."),
        (" removal failed and was rolled back as far as possible: ", ": a remoção falhou e foi desfeita na medida do possível: "),
        (" update failed and was rolled back as far as possible: ", ": a atualização falhou e foi desfeita na medida do possível: "),
        (" install failed and was rolled back as far as possible: ", ": a instalação falhou e foi desfeita na medida do possível: "),
        (" install rolled back: ", ": a instalação foi desfeita: "),
        (" removal failed: ", ": a remoção falhou: "),
        (" install failed: ", ": a instalação falhou: "),
        ("Ultimate ASI Loader update failed: ", "Falha na atualização do Ultimate ASI Loader: "),
        ("ReShade Addon Manager failed: ", "Falha no ReShade Addon Manager: "),
        ("Protected modified file: ", "Arquivo modificado protegido: "),
        (" no longer matches the RHI-managed hash, so it was not deleted.", " não confere mais com o hash gerenciado pelo RHI, por isso não foi apagado."),
        // Start-up recovery of a rolled-back removal puts a restored Wine override back after Wine exits.
        (RHI.Platform.Linux.Deployment.WineOverrideReapplyCompensator.DeferredPrefix,
         "O Wine está em execução neste prefixo e sobrescreveria a alteração ao sair; por isso este override do Wine será reaplicado na próxima inicialização: "),
        // w3/scan rows
        ("The app that installed this game is working on it (installing, updating, preparing or running it); RHI changes nothing until it has finished.", "O app que instalou este jogo está trabalhando nele (instalando, atualizando, preparando ou executando); o RHI não altera nada até ele terminar."),
        ("The job list of the app that installed this game could not be read, so RHI cannot tell whether that app is changing it. File: ", "Não foi possível ler a lista de tarefas do app que instalou este jogo, então o RHI não sabe se esse app está alterando o jogo. Arquivo: "),
        ("This game was installed by a copy of the Xbox app RHI does not know, so it cannot tell whether that app is changing it. Nothing was changed. Start that app once, or add its folder in Settings → Xbox / Game Pass folders. Folder: ", "Este jogo foi instalado por uma cópia do app do Xbox que o RHI não conhece, então não sabe se esse app está alterando o jogo. Nada foi alterado. Abra esse app uma vez ou adicione a pasta dele em Configurações → Pastas do Xbox / Game Pass. Pasta: "),
        ("An instance file of the Xbox app uses a newer format than this RHI reads (update RHI); that instance is not read from it. File: ", "Um arquivo de instância do app do Xbox usa um formato mais novo do que este RHI lê (atualize o RHI); essa instância não é lida dele. Arquivo: "),
        (RHI.Core.Services.XboxWriteGate.EncryptedExecutableMessage, "Não foi possível determinar se o executável do jogo é de 32 ou 64 bits: este pacote do Xbox o mantém criptografado no disco e não declara sua arquitetura, então o RHI não pode instalar mods nele."),
        ("RHI never changes this file of an Xbox package: the game would no longer start, or the app that installed it would put it back. File: ", "O RHI nunca altera este arquivo de um pacote Xbox: o jogo deixaria de iniciar, ou o app que o instalou o restauraria. Arquivo: "),
        // w3/parity rows
        (RHI.Platform.Linux.Deployment.DownloadSources.NexusApi.RefusalPrefix,
            "Download recusado: links da API do Nexus Mods devem ser HTTPS em um host confiável (api.nexusmods.com). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.NexusFiles.RefusalPrefix,
            "Download recusado: links de arquivos do Nexus Mods devem ser HTTPS em um host confiável (servidores de arquivos do Nexus Mods, nexus-cdn.com). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.NexusGameList.RefusalPrefix,
            "Download recusado: links da lista de jogos do Nexus Mods devem ser HTTPS em um host confiável (data.nexusmods.com). Link recusado: "),
        (RHI.Platform.Linux.Network.NexusApiClient.UnreachableMessage, "Não foi possível falar com o Nexus Mods."),
        (RHI.Platform.Linux.Network.NexusDownloadService.NoLinkMessage, "O Nexus Mods não deu nenhum link de download que o RHI aceite para este arquivo."),
        (RHI.Platform.Linux.Network.NexusApiClient.NoAccountMessage, "O Nexus Mods respondeu à verificação da chave sem uma conta."),
        (RHI.Platform.Linux.Network.NexusApiClient.OversizedMessage, "O Nexus Mods enviou mais do que uma resposta de dados."),
        (RHI.Platform.Linux.Network.NexusApiClient.AnsweredPrefix, "O Nexus Mods respondeu "),
        (RHI.Platform.Linux.Settings.ShaderProfileArchive.TooLargeMessage, "A exportação do perfil de shaders passa do limite (512 MiB)."),
        (RHI.Platform.Linux.Settings.ShaderProfileArchive.NotAProfileMessage, "Este zip não é um perfil de shaders (não tem shader_import.json)."),
        (RHI.Platform.Linux.Settings.ShaderProfileArchive.UnreadableProfileMessage, "O perfil de shaders deste zip não pode ser lido."),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.NoOwnerMessage, "O NR Cost Scaler precisa antes de um método de Neural Rendering instalado pelo RHI (ShortFuse, DLSS5 Feeder, OptiScaler DLSS NR ou OptiScaler F5 DLSSNR)."),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.OwnerModifiedPrefix, "NR Cost Scaler não instalado: o nvngx_dlssnr.dll não confere mais com o que o RHI instalou para "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.AlreadyInstalledMessage, "O NR Cost Scaler já está instalado para este jogo."),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.NotInstalledMessage, "O NR Cost Scaler não foi instalado pelo RHI para este jogo."),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.ForeignFilePrefix, "NR Cost Scaler não instalado: um arquivo que o RHI não gravou está no caminho: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.ScalerModifiedPrefix, "NR Cost Scaler não removido: seus arquivos mudaram desde a instalação pelo RHI (mantidos como estão): "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.DownloadFailedPrefix, "O download do NR Cost Scaler falhou; nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.PackageIncompleteMessage, "A versão do NR Cost Scaler não contém nvngx_dlssnr.dll e nvngx_dlssnr.ini; nada foi alterado."),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.InstalledPrefix, "NR Cost Scaler instalado sobre "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.RemovedMessage, "NR Cost Scaler removido; o runtime de Neural Rendering voltou a ser como era."),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.FailedRolledBackPrefix, "O NR Cost Scaler falhou e foi desfeito: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.OwnerBlockedPrefix, "Remova o NR Cost Scaler antes: ele envolve o nvngx_dlssnr.dll de "),
        (RHI.Platform.Linux.Deployment.DownloadSources.NrCostScaler.RefusalPrefix,
            "Download recusado: links do NR Cost Scaler devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        // w3/vmsplit rows
        // w3/dllnames rows
        // #124 Control UE: an earlier copy kept beside a DLL the user changed; a user change an update leaves alone.
        ("an earlier copy RHI kept from a previous Control UE install is in the way; move it aside and reinstall RenoDX to update this DLL: ",
            "uma cópia anterior que o RHI guardou de uma instalação anterior do Control UE está no caminho; mova-a para outro lugar e reinstale o RenoDX para atualizar esta DLL: "),
        (" The copy RHI kept from before its install stays at ", " A cópia que o RHI guardou de antes da instalação continua em "),
        (": changed outside RHI since the install; kept, not applied again.", ": alterado fora do RHI desde a instalação; mantido, não aplicado de novo."),
        ("RenoDX installed: ", "RenoDX instalado: "),
        // #170: add-ons turned off/on with ReShade's No Addons channel.
        (RHI.Platform.Linux.Deployment.ReShadeAddonSwitch.TurnedOffPrefix,
            " Add-ons do ReShade desligados porque o Sem add-ons não consegue carregá-los (ficam na pasta do jogo; voltam a ligar quando o ReShade voltar para Estável ou Desenvolvimento): "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonSwitch.TurnedOnPrefix, " Add-ons do ReShade religados: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonSwitch.NameTakenPrefix,
            "O ReShade não foi alterado: um add-on não pode ser desligado nem religado porque já existe um arquivo com o nome que ele receberia. Tire esse arquivo do lugar primeiro: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonSwitch.HeldPrefix,
            "Este add-on está desligado pela versão Sem add-ons do ReShade; instale antes neste jogo o ReShade com suporte a add-ons (Estável ou Desenvolvimento): "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonSwitch.ManagerUnreadableNote,
            " Os add-ons do Gerenciador de add-ons não foram verificados porque a lista salva dele não pode ser lida, então ficaram como estavam; aplique o ReShade de novo quando essa lista puder ser lida."),
        (RHI.Platform.Linux.Deployment.ReShadeAddonSwitch.OffNote,
            "Desligado pela versão Sem add-ons do ReShade: o add-on fica na pasta do jogo e volta a ligar quando o RHI instalar neste jogo o ReShade com suporte a add-ons (Estável ou Desenvolvimento)."),
        // #91 ReShade No Addons channel.
        ("ReShade on this game is the No Addons build, which cannot load add-ons; choose Stable or Nightly in the ReShade profile and apply it first: ",
            "O ReShade deste jogo é a versão Sem add-ons, que não carrega add-ons; escolha Estável ou Desenvolvimento no perfil do ReShade e aplique antes: "),
        ("ReShade No Addons update available: ", "Atualização do ReShade Sem add-ons disponível: "),
        ("ReShade No Addons is current: ", "O ReShade Sem add-ons está atualizado: "),
        ("Could not find the current ReShade download without add-on support on reshade.me.", "Não foi possível encontrar no reshade.me o download atual do ReShade sem suporte a add-ons."),
        // #120 ReShade DLL name.
        ("The chosen ReShade DLL name is for the other bitness of Windows game; choose another name: ",
            "O nome de DLL do ReShade escolhido é para a outra arquitetura de jogo Windows; escolha outro nome: "),
        ("ReShade is already in this game folder, but RHI Linux did not install it; a second ReShade would load twice, so nothing was installed. Remove that ReShade first, or keep using it: ",
            "O ReShade já está na pasta deste jogo, mas não foi instalado pelo RHI Linux; um segundo ReShade carregaria duas vezes, então nada foi instalado. Remova esse ReShade antes ou continue usando-o: "),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ExternalReShadeBlocksDependencyPrefix,
            "Este componente precisa do ReShade instalado pelo RHI, mas um ReShade que o RHI Linux não instalou já está na pasta deste jogo; ele foi mantido e nada foi instalado. Remova esse ReShade antes ou continue usando-o sem este componente. Componente e arquivo: "),
        ("Another component RHI installed on this game already uses this DLL name, so ReShade cannot be installed under it; choose another DLL name for ReShade. Name and component: ",
            "Outro componente que o RHI instalou neste jogo já usa este nome de DLL, então o ReShade não pode ser instalado com ele; escolha outro nome de DLL para o ReShade. Nome e componente: "),
        (RHI.Platform.Linux.Deployment.Dx9TranslationReShadeCoordinator.NeedsReShadeAsD3d9Prefix,
            "A tradução de DirectX 9 (dgVoodoo2) precisa do ReShade do RHI instalado como d3d9.dll; nada foi alterado. Remova o ReShade, defina o nome da DLL como d3d9.dll, instale o ReShade de novo e tente outra vez. O ReShade está instalado agora como: "),
        // Phase B (#23/#31 journaling): a failed removal is rolled back whole and says so.
        ("OptiScaler removal failed and was rolled back; OptiScaler stays installed: ",
            "A remoção do OptiScaler falhou e foi desfeita; o OptiScaler continua instalado: "),
        ("ReShade removal failed and was rolled back; ReShade stays installed: ",
            "A remoção do ReShade falhou e foi desfeita; o ReShade continua instalado: "),
        // Phase B (#136): OptiScaler under another DLL name.
        ("OptiScaler cannot be installed under this DLL name; choose one of dxgi.dll, winmm.dll, d3d11.dll, d3d12.dll, dbghelp.dll, version.dll, wininet.dll or winhttp.dll: ",
            "O OptiScaler não pode ser instalado com este nome de DLL; escolha dxgi.dll, winmm.dll, d3d11.dll, d3d12.dll, dbghelp.dll, version.dll, wininet.dll ou winhttp.dll: "),
        ("Another component RHI installed on this game already uses this DLL name, so OptiScaler cannot be installed under it. Name and component: ",
            "Outro componente que o RHI instalou neste jogo já usa este nome de DLL, então o OptiScaler não pode ser instalado com ele. Nome e componente: "),
        // #172: the saved DLL name was skipped because another RHI component holds it.
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.OptiScalerSavedNameSkippedPrefix,
            " O nome de DLL salvo para o OptiScaler é usado por outro componente que o RHI instalou neste jogo, então o OptiScaler foi instalado com o nome automático. Nome salvo e componente: "),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.OptiScalerAlreadyPresentPrefix,
            "Já existe na pasta deste jogo um OptiScaler que o RHI não instalou, então o RHI não instala um segundo (os dois seriam carregados); nada foi alterado. Remova esse OptiScaler primeiro e tente de novo. Arquivo: "),
        // #173 (w3 final review nits 2 and 4).
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.OptiScalerHeldByRhiComponentPrefix,
            "Outro componente que o RHI instalou neste jogo já trouxe um OptiScaler, então o RHI não instala um segundo (os dois seriam carregados); nada foi alterado. Remova esse componente primeiro e tente de novo. Arquivo e componente: "),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.FolderUnreadablePrefix,
            "Não foi possível ler a pasta do jogo para procurar outro OptiScaler, então nada foi instalado nem alterado: "),
        // #136 Control UE OptiScaler frame generation step.
        ("Control UE frame generation set up: OptiScaler Nightly pinned as winmm.dll with Streamline, DLSS Enabler and the frame generation settings. Build: ",
            "Geração de quadros do Control UE configurada: OptiScaler Nightly fixado como winmm.dll com Streamline, DLSS Enabler e as configurações de geração de quadros. Build: "),
        ("Control UE frame generation was not set up; the RenoDX install is kept: ",
            "A geração de quadros do Control UE não foi configurada; a instalação do RenoDX foi mantida: "),
        ("OptiScaler is already installed on this game under another build or DLL name, so it was left as it is. Remove OptiScaler, then install RenoDX again with frame generation. Installed: ",
            "O OptiScaler já está instalado neste jogo com outro build ou nome de DLL, então foi deixado como está. Remova o OptiScaler e instale o RenoDX de novo com a geração de quadros. Instalado: "),
        (" Streamline/DLSS Enabler could not be deployed: ", " Não foi possível implantar Streamline/DLSS Enabler: "),
        (" The frame generation settings could not be written to OptiScaler.ini: ", " Não foi possível gravar as configurações de geração de quadros no OptiScaler.ini: "),
        (" ReShade keeps its own DLL name (upstream moves it to dxgi.dll; RHI does not rename an installed ReShade): ",
            " O ReShade mantém o próprio nome de DLL (o upstream o move para dxgi.dll; o RHI não renomeia um ReShade instalado): "),
        (" FgSlHdr10 set in reshade.ini: ", " FgSlHdr10 definido no reshade.ini: "),
        (" FgSlHdr10 was not set in reshade.ini: ", " FgSlHdr10 não foi definido no reshade.ini: "),
        ("reshade.ini was not found in the game folder.", "o reshade.ini não foi encontrado na pasta do jogo."),
        ("RenoDX is not installed by RHI on this game.", "o RenoDX não foi instalado pelo RHI neste jogo."),
        ("the value was changed outside RHI and was kept.", "o valor foi alterado fora do RHI e foi mantido."),
        // w4/encore (#158): RTX Encore service messages.
        (RHI.Platform.Linux.Deployment.DownloadSources.RtxEncore.RefusalPrefix,
            "Download recusado: links do RTX Encore devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (RtxEncoreText.NotYetConfirmedNote, "Ainda não confirmado no Linux: geração de quadros ×3–×6 e Neural Rendering podem funcionar pelo DLSS do Proton; Smooth Motion não (precisa do driver do Windows)."),
        (RtxEncoreText.NativeLinuxMessage, "O RTX Encore é uma DLL do Windows; não se aplica a jogos nativos do Linux."),
        (RtxEncoreText.NeedsX64Message, "O RTX Encore precisa de um jogo Windows de 64 bits."),
        (RtxEncoreText.ApiMessage, "O RTX Encore funciona apenas com jogos DirectX 11, DirectX 12 e Vulkan."),
        (RtxEncoreText.NeedsDlssMessage, "O RTX Encore funciona pelo DLSS do próprio jogo; este jogo não tem DLSS."),
        (RtxEncoreText.PrefixMessage, "Inicie o jogo uma vez para criar o prefixo Wine/Proton antes de instalar o RTX Encore."),
        (RtxEncoreText.BinaryFolderMessage, "A pasta do executável do jogo ainda não é conhecida; confirme o executável em Editar jogo antes de instalar o RTX Encore."),
        (RtxEncoreText.AlreadyInstalledMessage, "O RTX Encore já está instalado pelo RHI neste jogo."),
        (RtxEncoreText.NotInstalledMessage, "O RTX Encore não está instalado pelo RHI neste jogo."),
        (RtxEncoreText.NameLockedMessage, "O RTX Encore está instalado: remova-o para escolher outro nome."),
        (RtxEncoreText.NameNotOfferedPrefix, "O RTX Encore não pode usar este nome neste jogo; escolha outro no ⚙: "),
        (RtxEncoreText.NameHeldPrefix, "Este nome já pertence a outro componente do RHI; escolha outro no ⚙: "),
        (RtxEncoreText.KeptCopyInTheWayPrefix, "RTX Encore não instalado: uma cópia guardada antes está no caminho; mova-a para outro lugar primeiro: "),
        (RtxEncoreText.DownloadFailedPrefix, "O download do RTX Encore falhou; nada foi alterado: "),
        (RtxEncoreText.NeuralRenderingUnavailableMessage, "A lista do DLSS não oferece mais o arquivo de Neural Rendering 310.8.0 da NVIDIA, o único que o RTX Encore aceita; nada foi alterado."),
        (RtxEncoreText.NeuralRenderingMismatchMessage, "O arquivo de Neural Rendering 310.8.0 baixado não é o que o RTX Encore aceita; nada foi alterado."),
        (RtxEncoreText.NeuralRenderingNeedsDlssListMessage, "A lista do DLSS não está disponível, então o arquivo de Neural Rendering não pode ser baixado; nada foi alterado."),
        (RtxEncoreText.FailedRolledBackPrefix, "O RTX Encore falhou e foi desfeito: "),
        (RtxEncoreText.ModifiedPrefix, "RTX Encore não alterado: um arquivo mudou desde a instalação pelo RHI (mantido como está): "),
        (RtxEncoreText.MissingPrefix, "RTX Encore não atualizado: o arquivo dele sumiu; remova-o e instale de novo: "),
        (RtxEncoreText.InstalledPrefix, "RTX Encore instalado; pressione Insert no jogo para abrir o menu. Ainda não confirmado no Linux. Nome: "),
        (RtxEncoreText.InstalledWithNeuralRenderingSuffix, " (com o arquivo de Neural Rendering 310.8.0)"),
        (RtxEncoreText.RemovedMessage, "RTX Encore removido; os arquivos guardados de antes voltaram."),
        (RtxEncoreText.KeptCopyMissingPrefix, " A cópia guardada de antes não foi encontrada, então nada foi reposto: "),
        (RtxEncoreText.UpToDatePrefix, "O RTX Encore está atualizado: "),
        (RtxEncoreText.UpdatedPrefix, "RTX Encore atualizado para "),
        (RtxEncoreText.UpdateAvailablePrefix, "Atualização do RTX Encore disponível: "),
        (RtxEncoreText.LatestPrefix, "RTX Encore mais recente: "),
        (RtxEncoreText.CheckFailedPrefix, "Não foi possível verificar o RTX Encore: "),
        (RtxEncoreText.NeuralRenderingOnMessage, "RTX Encore: arquivo de Neural Rendering 310.8.0 instalado."),
        (RtxEncoreText.NeuralRenderingOffMessage, "RTX Encore: arquivo de Neural Rendering removido; um arquivo guardado de antes voltou."),
        (RtxEncoreText.NeuralRenderingAlreadyMessage, "RTX Encore: nada a alterar."),
        (RtxEncoreText.SavedWithNeuralRenderingMessage, "Salvo: o RTX Encore será instalado com o arquivo de Neural Rendering 310.8.0."),
        (RtxEncoreText.SavedWithoutNeuralRenderingMessage, "Salvo: o RTX Encore será instalado sem o arquivo de Neural Rendering."),
        (RtxEncoreText.SavedNamePrefix, "Salvo: o RTX Encore será instalado como "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.RtxEncoreOwnerMessage, "O NR Cost Scaler não pode envolver o arquivo de Neural Rendering do RTX Encore: o RTX Encore só aceita o arquivo 310.8.0 original da NVIDIA."),
        (RtxEncoreText.ReleaseListShapeMessage, "Não foi possível ler a lista de versões do RTX Encore."),
        (RtxEncoreText.NoReleaseMessage, "Nenhuma versão do RTX Encore com um RTX-Encore-*.zip foi encontrada."),
        (RtxEncoreText.NoPublishedDigestPrefix, "A versão do RTX Encore não publica SHA-256 (digest do GitHub nem SHA256SUMS); recusada: "),
        (RtxEncoreText.TagNotAVersionPrefix, "A tag da versão do RTX Encore não é um número de versão: "),
        (RtxEncoreText.SumsNoEntryPrefix, "O SHA256SUMS do RTX Encore não tem linha para o zip: "),
        (RtxEncoreText.SumsMismatchPrefix, "O zip do RTX Encore não corresponde ao seu SHA256SUMS: "),
        (RtxEncoreText.PayloadNotAtRootMessage, "O rtx-encore.dll não está exatamente uma vez na raiz do zip do RTX Encore."),
        (RtxEncoreText.TooSmallMessage, "A DLL do RTX Encore é pequena demais para ser plausível."),
        (RtxEncoreText.NotX64Message, "O download do RTX Encore não é uma DLL Windows de 64 bits."),
        (RtxEncoreText.NoVersionResourceMessage, "O download do RTX Encore não traz as informações de versão do RTX Encore."),
        // #98 OptiPatcher.
        (RHI.Platform.Linux.Deployment.DownloadSources.OptiPatcher.RefusalPrefix,
            "Download recusado: links do OptiPatcher devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        (" OptiPatcher deployed to plugins/ (OptiScaler LoadAsiPlugins on). Build: ", " OptiPatcher implantado em plugins/ (LoadAsiPlugins do OptiScaler ativado). Build: "),
        (" OptiPatcher was not deployed; OptiScaler works without it: ", " O OptiPatcher não foi implantado; o OptiScaler funciona sem ele: "),
        ("OptiPatcher update available: ", "Atualização do OptiPatcher disponível: "),
        ("a plugins/OptiPatcher.asi RHI does not own is already there; it was left as it is.", "já existe um plugins/OptiPatcher.asi que não pertence ao RHI; ele foi deixado como está."),
        ("The OptiPatcher rolling release lists no OptiPatcher.asi download.", "A versão contínua do OptiPatcher não lista o download OptiPatcher.asi."),
        ("The downloaded OptiPatcher.asi is not a 64-bit Windows DLL.", "O OptiPatcher.asi baixado não é uma DLL Windows de 64 bits."),
        // #97 OptiScaler per-GPU INI templates.
        (" OptiScaler.ini comes from your INI template: ", " O OptiScaler.ini vem do seu modelo de INI: "),
        (" The OptiScaler INI template could not be prepared, so the package's OptiScaler.ini was used: ",
            " Não foi possível preparar o modelo de INI do OptiScaler, então foi usado o OptiScaler.ini do pacote: "),
        // w5/update-policy rows
        // #177 Follow newest DLSS / Streamline: history notes and per-file failures.
        (RHI.Platform.Linux.Deployment.DlssAutoFollowService.AutomaticDlssNote, "Automático: Seguir o DLSS mais recente está ligado."),
        (RHI.Platform.Linux.Deployment.DlssAutoFollowService.AutomaticStreamlineNote, "Automático: Seguir o Streamline mais recente está ligado."),
        (RHI.Platform.Linux.Deployment.DlssAutoFollowService.RefusedPrefix, "Não seguido: "),
        (RHI.Platform.Linux.Deployment.DlssAutoFollowService.DownloadFailedPrefix, "Não foi possível baixar a versão mais recente: "),
        (RHI.Platform.Linux.Deployment.DlssAutoFollowService.NotInManifestPrefix, "Não está mais no manifesto de DLSS: "),
        (RHI.Platform.Linux.Deployment.DlssAutoFollowService.SwapFailedMessage, "A troca falhou; o arquivo do jogo foi mantido."),
        // #178
        (RHI.Platform.Linux.Deployment.LinuxUpdateAllService.LeftOutMessage, "Deixado de fora de Atualizar tudo pela inclusão em atualizações."),
        (RHI.Platform.Linux.Deployment.LinuxUpdateAllService.LeftOutUnreadableMessage, "Deixado de fora de Atualizar tudo: não é possível ler o arquivo de inclusão em atualizações."),
        // w5/catalogue-info rows
        // #183 card links: one row per download source's refusal text.
        (RHI.Platform.Linux.Deployment.DownloadSources.UltraPlusGameList.RefusalPrefix,
            "Download recusado: links da lista de jogos do Ultra+ devem ser HTTPS em um host confiável (theultraplace.com). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.UltrawideFixList.RefusalPrefix,
            "Download recusado: links da lista de correções ultrawide devem ser HTTPS em um host confiável (codeberg.org). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.CommunityGameList.RefusalPrefix,
            "Download recusado: links da lista de jogos da comunidade devem ser HTTPS em um host confiável (raw.githubusercontent.com). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.SteamStoreSearch.RefusalPrefix,
            "Download recusado: links da busca da loja Steam devem ser HTTPS em um host confiável (store.steampowered.com). Link recusado: "),
        // #184 info dialogs.
        (RHI.Platform.Linux.Deployment.DownloadSources.OptiScalerWiki.RefusalPrefix,
            "Download recusado: links do wiki do OptiScaler devem ser HTTPS em um host confiável (github.com). Link recusado: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.ComponentNotes.RefusalPrefix,
            "Download recusado: links de notas dos componentes devem ser HTTPS em um host confiável (raw.githubusercontent.com). Link recusado: "),
        // w5/ini-presets rows
        // #181 INI templates and deploys.
        (RHI.Platform.Linux.Deployment.UserIniService.NativeGamePrefix, "Os arquivos INI não foram gravados: este é um jogo nativo do Linux: "),
        (RHI.Platform.Linux.Deployment.UserIniService.NoFolderPrefix, "Os arquivos INI não foram gravados: a pasta do jogo não está disponível: "),
        (RHI.Platform.Linux.Deployment.UserIniService.NotManagedPrefix, "Os arquivos INI não foram gravados: o RHI não instalou este componente neste jogo, e uma cópia que ele não instalou fica como está: "),
        (RHI.Platform.Linux.Deployment.UserIniService.DeployedPrefix, "INI implantado do seu modelo: "),
        (RHI.Platform.Linux.Deployment.UserIniService.DeployedBackupPrefix, "INI implantado do seu modelo; o arquivo anterior do jogo fica ao lado como: "),
        (RHI.Platform.Linux.Deployment.UserIniService.ChangedPrefix, "INI não gravado: o arquivo do jogo mudou desde que o RHI o gravou (ou o RHI não o gravou); confirme para substituí-lo, uma cópia de segurança é mantida: "),
        (RHI.Platform.Linux.Deployment.UserIniService.RestoredPrefix, "Seu INI anterior voltou: "),
        (RHI.Platform.Linux.Deployment.UserIniService.NoBackupPrefix, "Não resta nenhuma cópia de segurança de INI guardada pelo RHI para restaurar: "),
        (RHI.Platform.Linux.Deployment.UserIniService.RestoreChangedPrefix, "A cópia de segurança não foi restaurada: o arquivo do jogo mudou desde que o RHI o gravou; a cópia fica ao lado dele: "),
        (RHI.Platform.Linux.Deployment.UserIniTemplates.TooLargePrefix, "Modelo de INI ignorado: ele tem mais de 1 MiB: "),
        (RHI.Platform.Linux.Deployment.IniLines.NotPutBackPrefix, " Estes arquivos já tinham sido gravados e não puderam ser restaurados: "),
        (RHI.Platform.Linux.Deployment.IniLines.LinkRefusalPrefix, "Link de INI não seguido: o destino deve ser um arquivo com o mesmo nome dentro da pasta do jogo. Link: "),
        // #180 ReShade presets.
        (RHI.Platform.Linux.Deployment.ReShadePresetService.NotAPresetPrefix, "Não é uma predefinição do ReShade (precisa de uma linha Techniques= com pelo menos um efeito .fx): "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.TooLargePrefix, "Predefinição não usada: ela tem mais de 1 MiB: "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.NoManagedReShadePrefix, "Instale o ReShade com o RHI primeiro; as predefinições vão para jogos cujo ReShade o RHI gerencia: "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.NameTakenPrefix, "Já existe na pasta do jogo um arquivo com o nome da predefinição que o RHI não colocou ali (ou que você alterou), então ele ficou como está: "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.InstalledPrefix, "Predefinição instalada e selecionada no ReShade.ini: "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.RemovedPrefix, "Predefinição removida: "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.KeptChangedPrefix, "Predefinição tirada da lista do RHI; o arquivo ficou porque mudou desde que o RHI o colocou ali: "),
        (RHI.Platform.Linux.Deployment.ReShadePresetService.NotInstalledPrefix, "O RHI não instalou esta predefinição: "),
        // #189 Luma settings.
        (RHI.Platform.Linux.Deployment.LumaSettingsService.NotManagedPrefix, "As configurações do Luma precisam do Luma instalado pelo RHI: "),
        (RHI.Platform.Linux.Deployment.LumaSettingsService.NoEngineIniPrefix, "Nenhum local do Engine.ini foi encontrado para este jogo, então o TAA não foi alterado: "),
        // #182 peak brightness.
        (RHI.Platform.Linux.Deployment.RenoDxPeakNits.NotManagedPrefix, "O brilho máximo vai para as predefinições do RenoDX; instale o RenoDX com o RHI primeiro: "),
        (RHI.Platform.Linux.Deployment.RenoDxPeakNits.NoReShadeIniPrefix, "Este jogo ainda não tem ReShade.ini; inicie-o uma vez com o RenoDX para que ele grave suas predefinições: "),
        // w5/shell-launch rows
        // #191 display switch (platform messages; the tool's own text or the size follows).
        (RHI.Platform.Linux.Hdr.DisplayModeSwitcher.ToolFailedPrefix, "A ferramenta de tela informou um erro: "),
        (RHI.Platform.Linux.Hdr.DisplayModeSwitcher.NoModePrefix, "A tela não oferece esta resolução: "),
        (RHI.Platform.Linux.Hdr.DisplayModeSwitcher.ToolMissingPrefix, "A ferramenta de tela não está instalada: "),
        (RHI.Platform.Linux.Hdr.DisplayModeSwitcher.StateUnsavedPrefix, "O RHI não conseguiu registrar o modo de tela atual, então não o mudou: "),
        (RHI.Platform.Linux.Hdr.DisplayModeSwitcher.UnsafeAnswer, "A ferramenta de tela indicou uma saída ou modo que o RHI não aceita, então a tela não foi mudada."),
        // w6/crash rows
        // #194: Addon Manager add-ons in Update All.
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CurrentMessage, "Os add-ons do ReShade Addon Manager estão atualizados."),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.UpdateAvailablePrefix, "Atualização disponível para add-ons do ReShade Addon Manager: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.CouldNotCheckPrefix, "Não foi possível verificar os add-ons do ReShade Addon Manager: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.ListUnavailableReason, "não foi possível baixar a lista de add-ons."),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.NoFilesRecordedReason, "nenhum arquivo de add-on está registrado para os add-ons selecionados."),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.NotInListPrefix, "não está mais na lista de add-ons: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.NoValidatorPrefix, "o download não oferece nada para comparar: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.UpdatedPrefix, "Add-ons do ReShade Addon Manager atualizados: "),
        (RHI.Platform.Linux.Deployment.ReShadeAddonManagerService.ReconcileFailedPrefix,
            "O ReShade Addon Manager não alterou nada: não foi possível levar a lista salva dele para os registros de instalação: "),
        // w5/reshade-core rows
        // #92 ReShade as a Vulkan layer.
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.RunnerTooOldPrefix, "A camada Vulkan do ReShade precisa do Proton ou do Wine 8 ou mais novo. O prefixo deste jogo foi criado por: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.GameShipsLoaderPrefix,
            "Este jogo tem o próprio vulkan-1.dll ao lado do executável; o RHI não o substitui pelo carregador Vulkan de que o ReShade precisa, então nada foi instalado. Arquivo: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LayerFolderTakenPrefix, "Algo que o RHI não instalou já está onde a camada Vulkan do ReShade fica, então nada foi instalado: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.InstalledPrefix, "ReShade (camada Vulkan) instalado; ReShade.ini ao lado do jogo. Versão: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.UpdatedPrefix, "ReShade (camada Vulkan) atualizado. Versão: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderUpdatedNote, " O carregador Vulkan também foi atualizado."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderCheckSkippedNote, " Não foi possível acessar a LunarG, então o carregador Vulkan foi mantido como estava."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderRestoredNote, " O carregador Vulkan ao lado do jogo estava faltando e foi reposto."),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.LoaderMissingNotRestoredPrefix,
            "O carregador Vulkan ao lado do jogo está faltando e o RHI não conseguiu baixá-lo de novo, então nada foi alterado; tente de novo quando a LunarG estiver acessível: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.FailedRolledBackPrefix, "O ReShade (camada Vulkan) falhou e foi revertido; nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.ModifiedFilePrefix, "Um arquivo da camada Vulkan do ReShade foi alterado fora do RHI, então foi mantido como está e nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.ReShadeVulkanLayer.NeedsDllReShadePrefix,
            "O ReShade está instalado neste jogo como camada Vulkan, e este componente só funciona com o ReShade como DLL ao lado do jogo, então nada foi instalado: "),
        (RHI.Platform.Linux.Deployment.WineRegistryEditor.MissingSystemHiveMessage,
            "O system.reg do Wine/Proton não foi encontrado. Inicie o jogo uma vez para que o prefixo seja inicializado."),
        (RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LatestUnavailableMessage, "Não foi possível encontrar a versão atual do carregador Vulkan no site da LunarG."),
        (RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LoaderMissingPrefix, "O download do runtime Vulkan da LunarG não contém o carregador Vulkan para este jogo: "),
        (RHI.Platform.Linux.Deployment.VulkanRuntimeProvider.LoaderInvalidPrefix, "O carregador Vulkan baixado não é um carregador Vulkan válido para este jogo: "),
        (RHI.Platform.Linux.Deployment.DownloadSources.LunarGVulkanRuntime.RefusalPrefix,
            "Download recusado: links do runtime Vulkan devem ser HTTPS em um host confiável (LunarG). Link recusado: "),
        // w7/smalls rows
        // #199 NR Cost Scaler in Update All.
        (RHI.Platform.Linux.Deployment.NrCostScalerService.CouldNotCheckPrefix, "NR Cost Scaler: não foi possível verificar a versão mais recente: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.UpToDatePrefix, "O NR Cost Scaler está em dia: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.UpdateAvailablePrefix, "Atualização do NR Cost Scaler disponível: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.UpdatedPrefix, "NR Cost Scaler atualizado: "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.NotUpdatedModifiedPrefix, "NR Cost Scaler não atualizado: seus arquivos mudaram desde a instalação pelo RHI (mantidos como estão): "),
        (RHI.Platform.Linux.Deployment.NrCostScalerService.IniKeptNote, " O seu próprio nvngx_dlssnr.ini foi mantido."),
        // #195 a dropped or watched RenoDX add-on.
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.LocalPackageCheckMessage, "O RenoDX foi instalado a partir de um arquivo local; Atualizar tudo não consegue verificá-lo."),
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.LocalInstalledPrefix, "RenoDX instalado a partir de um arquivo local: "),
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.LocalNotAnAddonPrefix, "Não é um add-on do RenoDX que o RHI possa instalar (um arquivo de add-on do ReShade: .addon64, .addon32 ou .addon): "),
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.LocalLedgerUnreadablePrefix, "Instalação do RenoDX recusada: os registros de instalação do RHI não podem ser lidos, então nada foi gravado. Cópia dos registros: "),
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.Local32BitOn64BitMessage, "Este add-on do RenoDX é de 32 bits e o jogo é de 64 bits; ele precisa do arquivo .addon64 do jogo."),
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.Local64BitOn32BitMessage, "Este add-on do RenoDX é de 64 bits e o jogo é de 32 bits; ele precisa do arquivo .addon32 do jogo."),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.DroppedAddonInstalledPrefix, "Add-on instalado para este jogo; Shaders/Add-ons o lista (desmarque-o lá para retirá-lo): "),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ReShadeDependencyBesideDxvkHdrMessage,
            "O DX9 HDR está instalado neste jogo, então o RHI não adiciona o ReShade a ele por conta própria: ao lado do DX9 HDR, o ReShade vai para a camada Vulkan dele. Instale primeiro o ReShade pela linha dele e tente de novo."),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.DroppedAddonNeedsReShadeMessage, "Este jogo não tem ReShade com suporte a add-ons, então o add-on não foi instalado e nada foi copiado. Instale o ReShade (não a versão No Addons) neste jogo primeiro."),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.DroppedAddon32BitOn64BitMessage, "Este add-on é de 32 bits e o jogo é de 64 bits; ele precisa do arquivo .addon64 do add-on."),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.DroppedAddon64BitOn32BitMessage, "Este add-on é de 64 bits e o jogo é de 32 bits; ele precisa do arquivo .addon32 do add-on."),
        (RHI.Platform.Linux.Deployment.RenoDxDeploymentService.LocalControlUeMessage, "O add-on do RenoDX para Control precisa dos passos extras do RHI: instale-o com o botão Instalar da linha do RenoDX."),
        // w7/reshade rows
        (RHI.Platform.Linux.Deployment.ProxyRename.NotInstalledPrefix, "Nada foi renomeado: o RHI não instalou este componente neste jogo: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.NameKeptPrefix, "A DLL já tem este nome; a escolha foi salva: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.ChangedPrefix, "A DLL não foi renomeada: o arquivo mudou desde que o RHI o instalou. Arquivo: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.OriginalKeptPrefix,
            "A DLL não foi renomeada: o RHI guarda o arquivo original do jogo com esse nome como cópia de segurança, então o nome precisa ficar; remova o componente e instale-o de novo com o novo nome. Arquivo: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.TargetTakenPrefix, "A DLL não foi renomeada: outro arquivo já usa esse nome na pasta do jogo. Arquivo: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.TargetHeldPrefix, "A DLL não foi renomeada: outro componente que o RHI instalou neste jogo usa esse nome. Nome e componente: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.NoPrefixMessage, "A DLL não foi renomeada: o prefixo Wine/Proton do jogo não está preparado; inicie o jogo uma vez primeiro."),
        (RHI.Platform.Linux.Deployment.ProxyRename.RenamedPrefix, "DLL renomeada no lugar; o override do Wine e o registro do RHI vão junto com ela: "),
        (RHI.Platform.Linux.Deployment.ProxyRename.FailedPrefix, "A renomeação da DLL falhou e foi revertida; a DLL mantém o nome: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.RenameNotInPlacePrefix,
            "A DLL do ReShade não pode ser renomeada no lugar enquanto outro componente decide o nome dela (OptiScaler, ShortFuse ou a tradução DirectX 9); remova o ReShade e instale-o de novo para mudar: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.RenameNameNotOfferedPrefix, "A DLL não foi renomeada: o ReShade não é oferecido com este nome para este jogo: "),
        (RHI.Platform.Linux.Deployment.OptiScalerDeploymentService.ChainNameTakenPrefix,
            "O OptiScaler não foi removido: o ReShade do RHI (ReShade64.dll) volta para o nome dele, mas outro arquivo usa esse nome agora; remova o ReShade primeiro ou libere esse nome. Nome: "),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.DllNamesResetMessage, "Todos os nomes de DLL deste jogo voltaram para automático."),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.RowNote,
            "Jogo XNA: o ReShade é instalado como d3d9.dll na pasta do jogo e na pasta de assemblies .NET do prefixo Wine/Proton dele, onde o XNA o carrega. Use apenas em títulos single-player/offline."),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.NativeLinuxNote,
            "Esta é a versão nativa para Linux do jogo, que não usa XNA, então o ReShade não pode ser adicionado a ela. A versão para Windows rodando pelo Proton pode receber o ReShade."),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.PrefixUnknownMessage,
            "O ReShade deste jogo XNA vai para o prefixo Wine/Proton dele, que ainda não está preparado; inicie o jogo uma vez para preparar o prefixo e depois instale o ReShade."),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.FolderMissingPrefix,
            "O ReShade deste jogo XNA vai para a pasta de assemblies .NET do prefixo dele, que ainda não existe; inicie o jogo uma vez para preparar o prefixo e depois instale o ReShade. Pasta: "),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.OutsidePrefixPrefix, "A pasta de assemblies XNA indicada pelo manifesto do RHI levaria para fora do prefixo deste jogo, então nada foi instalado: "),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.ForeignFilePrefix, "Um arquivo que o RHI não instalou já está na pasta de assemblies XNA, então nada foi instalado: "),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.ForeignCopyAfterMovePrefix,
            "Nada foi alterado: a cópia do ReShade do RHI não está mais no prefixo antigo, e a pasta de assemblies .NET do prefixo que o jogo usa agora tem um d3d9.dll que o RHI não instalou. Tire esse arquivo da pasta, copie o d3d9.dll da pasta do jogo (o ReShade do RHI) no lugar dele e escolha Reparar após mover de novo. Arquivo: "),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.MissingCopyAfterMovePrefix,
            "Nada foi alterado: a cópia do ReShade do RHI não está mais no prefixo antigo, e a pasta de assemblies .NET do prefixo que o jogo usa agora não tem d3d9.dll. Copie o d3d9.dll da pasta do jogo (o ReShade do RHI) para essa pasta e escolha Reparar após mover de novo. Pasta: "),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.ChangedCopyPrefix, "A cópia do ReShade na pasta de assemblies XNA mudou desde que o RHI a colocou, então não foi substituída: "),
        (RHI.Platform.Linux.Deployment.XnaGacReShade.ModeSuffix, " (pasta do jogo e pasta de assemblies .NET do prefixo, para XNA)"),
        // w7/shader-sets rows
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ShaderChoiceNotSavedPrefix,
            "Nada foi alterado: a escolha de shaders e add-ons do jogo não pôde ser salva: "),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.AddonSetNotAppliedPrefix, " Os add-ons do jogo não foram aplicados: "),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.AddonSetAppliedNote, " Os add-ons do jogo foram aplicados."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.ShaderSyncFailedPrefix, "Os pacotes de shaders não foram alterados: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.ShaderSyncDonePrefix, "Pacotes de shaders atualizados; o próprio ReShade não foi alterado. Pacotes: "),
        (RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ShaderPacksNotInstalledNote,
            " Os pacotes de shaders do jogo não foram instalados: eles precisam de um D3DCompiler_47 nativo no prefixo Wine. Instale-o com o Protontricks ou o Winetricks na linha D3DCompiler 47 da seção ReShade e depois use Aplicar em Shaders/Add-ons; uma DLL escolhida ali com Procurar passa a valer com o Aplicar do perfil do ReShade."),
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.NotOfferedMessage,
            "O DX9 HDR é oferecido só para jogos Windows que usam DirectX 9 (não para instalações Xbox / Game Pass)."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.PrefixNotStartedMessage,
            "O prefixo Wine/Proton do jogo ainda não foi criado. Inicie o jogo uma vez e feche-o; depois instale o DX9 HDR."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.BitnessUnknownMessage,
            "O RHI não consegue saber se o jogo é de 32 ou 64 bits, então o DX9 HDR não foi instalado. Defina a arquitetura em Editar jogo."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.AlreadyInstalledMessage,
            "O DX9 HDR já está instalado neste jogo."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.NotInstalledMessage,
            "O DX9 HDR não foi instalado pelo RHI neste jogo."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ForeignD3d9Prefix,
            "Há ao lado do jogo um d3d9.dll que o RHI não instalou; o RHI não o substitui, então nada foi instalado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.HeldByComponentPrefix,
            "O d3d9.dll ao lado do jogo pertence a outro componente do RHI, então o DX9 HDR não foi instalado. Remova-o primeiro: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.Dx9TranslationPrefix,
            "Este jogo usa a tradução DirectX 9 (dgVoodoo2) de outro componente do RHI, que precisa do d3d9.dll do jogo assim como o DX9 HDR, então nada foi alterado. Remova primeiro esse componente pela linha dele e depois instale o DX9 HDR. O componente: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.BlocksDx9TranslationMessage,
            "O DX9 HDR está instalado neste jogo e usa o d3d9.dll do jogo, de que a tradução DirectX 9 (dgVoodoo2) também precisa, então nada foi alterado. Remova primeiro o DX9 HDR pela linha dele e tente de novo."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ForeignConfPrefix,
            "Há ao lado do jogo um dxvk.conf que o RHI não gravou; o RHI não o sobrescreve, então nada foi instalado. Mova-o ou apague-o e tente de novo: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.DownloadFailedPrefix,
            "O download do DX9 HDR falhou; nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.UntrustedAssetPrefix,
            "O link da versão do DXVK HDR não está nas versões do dxvk de EndlesslyFlowering, então não foi baixado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.PackageIncompletePrefix,
            "A versão do DXVK HDR não contém o d3d9.dll de que este jogo precisa: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.NotDxvkHdrPrefix,
            "O d3d9.dll da versão do DXVK HDR não é uma build DXVK HDR para este jogo: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.UpdateNeedsReinstallMessage,
            "O DX9 HDR não foi atualizado: o d3d9.dll dele não está mais ao lado do jogo, ou o jogo não aceita mais o DX9 HDR. Remova o DX9 HDR e instale-o de novo se ainda quiser."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.DuplicateMemberPrefix,
            "A versão do DXVK HDR lista mais de uma vez o d3d9.dll de que este jogo precisa, então o RHI não o usou: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.D3d9TooLargePrefix,
            "O d3d9.dll da versão do DXVK HDR é maior do que o RHI aceita (64 MiB), então não foi usado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.InstalledPrefix,
            "DX9 HDR instalado (d3d9.dll e dxvk.conf ao lado do jogo). Versão: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ReShadeMovedNote,
            " O ReShade passou para a camada Vulkan dele neste jogo; remover o DX9 HDR o devolve como d3d9.dll."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.UpdatedPrefix,
            "DX9 HDR atualizado. Versão: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.AlreadyCurrentPrefix,
            "O DX9 HDR já é a build mais nova: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.RemovedMessage,
            "DX9 HDR removido; o jogo voltou a usar o DirectX 9 do runner."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ReShadeBackNote,
            " O ReShade voltou como d3d9.dll."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.KeptConfPrefix,
            " O dxvk.conf foi alterado depois que o RHI o gravou, então foi mantido: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.FailedRolledBackPrefix,
            "O DX9 HDR falhou e foi revertido; nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.RemovalFailedPrefix,
            "A remoção do DX9 HDR falhou e foi revertida; o DX9 HDR continua instalado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ModifiedFilePrefix,
            "O d3d9.dll do DX9 HDR foi alterado fora do RHI, então foi mantido como está e nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ConfChangedPrefix,
            "O dxvk.conf foi alterado depois que o RHI o gravou, então o RHI não o substituiu; edite-o você mesmo, ou remova o DX9 HDR e instale-o de novo: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.PresetSetMessage,
            "Predefinição do DX9 HDR alterada; o jogo a usa a partir do próximo início."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.UpdateAvailablePrefix,
            "Há uma build mais nova do DX9 HDR: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.CurrentPrefix,
            "O DX9 HDR está atualizado: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.CouldNotCheckPrefix,
            "Não foi possível verificar se há uma build mais nova do DX9 HDR: "),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.RowNote,
            "Troca o DirectX 9 do jogo pelo DXVK HDR só neste jogo (d3d9.dll e dxvk.conf ao lado do jogo). Remover desfaz tudo."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.RowReShadeNote,
            " O ReShade deste jogo passa para a camada Vulkan dele enquanto o DX9 HDR estiver instalado."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.HdrLaunchOffNote,
            " A execução HDR está desligada para este jogo: ligue-a (Execução HDR, nos detalhes do jogo) para ver HDR."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.ExternalNote,
            "Há ao lado do jogo um d3d9.dll DXVK HDR que o RHI não instalou; o RHI não mexe nele."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.MissingD3d9Note,
            "O d3d9.dll do DX9 HDR não está mais ao lado do jogo, então o DX9 HDR não faz nada agora. Remover tira o resto que o RHI preparou para ele; depois você pode instalá-lo de novo."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.WineD3dNote,
            " O comando de execução do jogo define PROTON_USE_WINED3D, então o Proton usa o DirectX 9 do próprio Wine e o DX9 HDR não tem efeito: tire isso do comando de execução (Editar jogo) para usar o DX9 HDR."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.DxvkHdrReShadeNamePrefix,
            "O ReShade está instalado neste jogo com outro nome de DLL, e o DX9 HDR só consegue passar para o caminho Vulkan um ReShade instalado como d3d9.dll, então nada foi alterado. Remova o ReShade pela linha dele, instale o DX9 HDR e depois instale o ReShade de novo. O ReShade está instalado agora como: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.DxvkHdrReShadeModifiedPrefix,
            "A DLL do ReShade do RHI foi alterada fora do RHI, então o DX9 HDR não a moveu e nada foi alterado: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.DxvkHdrReShadeOriginalPrefix,
            "O ReShade ocupou o lugar de um d3d9.dll que já existia antes de o RHI instalá-lo, então o DX9 HDR não pode usar esse nome e nada foi alterado. Remova primeiro o ReShade pela linha dele: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.DxvkHdrReShadeShortFuseMessage,
            "O ReShade deste jogo é carregado pelo ASI loader do ShortFuse, que o DX9 HDR não consegue mover, então nada foi alterado. Remova primeiro o ShortFuse."),
        (RHI.Platform.Linux.Deployment.DownloadSources.DxvkHdr.RefusalPrefix,
            "Download recusado: links do DXVK HDR devem ser HTTPS em um host confiável (GitHub). Link recusado: "),
        // w7 integration rows
        ("Wine/Proton prefix is not initialized; start the game once before enabling ReShade shader packs.",
            "O prefixo Wine/Proton não está preparado; inicie o jogo uma vez antes de ativar os pacotes de shaders do ReShade."),
        ("ReShade shader compiler compatibility check failed: ", "A verificação de compatibilidade do compilador de shaders do ReShade falhou: "),
        // w7 follow-up B30: the compiler check's refusals
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerGameFilesUnknownMessage,
            "O executável ou a pasta de binários do jogo ainda não é conhecido, então o RHI não consegue verificar o compilador de shaders dele."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerManagedMissingPrefix,
            "O RHI instalou o D3DCompiler_47.dll para este jogo, mas o arquivo sumiu: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerManagedInvalidPrefix,
            "O D3DCompiler_47.dll que o RHI instalou não é mais um compilador nativo da Microsoft: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerForeignBesideGamePrefix,
            "Um D3DCompiler_47.dll ao lado do jogo não é um compilador nativo da Microsoft, e o RHI não sobrescreve uma DLL que ele não instalou: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerSelectedInvalidPrefix,
            "O D3DCompiler_47.dll escolhido não existe, tem a arquitetura errada ou não é o compilador HLSL nativo da Microsoft: "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerNeededMessage,
            "Os pacotes de shaders do RHI no Wine/Proton precisam de um D3DCompiler_47.dll nativo da Microsoft; o compilador do próprio Wine não consegue compilar todos os efeitos do ReShade."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerSteamHintPrefix,
            " Para este jogo da Steam, instale-o no prefixo com o botão Protontricks da linha D3DCompiler 47, ou execute: protontricks "),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.CompilerWinetricksHint,
            " Instale-o neste prefixo com o botão Winetricks da linha D3DCompiler 47, ou escolha ali um D3DCompiler_47.dll da Microsoft com Procurar."),
        ("OptiScaler files were removed, but the name ReShade64.dll goes back to is unexpectedly occupied; RHI did not move ReShade back over it: ",
            "Os arquivos do OptiScaler foram removidos, mas o nome para o qual o ReShade64.dll volta está ocupado inesperadamente; o RHI não moveu o ReShade de volta por cima dele: "),
        ("Foreign RenoDX protection: another RenoDX add-on already exists at ", "Proteção contra RenoDX de terceiros: já existe outro add-on do RenoDX em "),
        (". RHI Linux will not overwrite or stack it automatically.", ". O RHI Linux não vai sobrescrevê-lo nem empilhá-lo automaticamente."),
        (RHI.Platform.Linux.Deployment.DxvkHdrDeploymentService.XnaGameMessage,
            "Este é um jogo XNA: ele carrega o d3d9.dll apenas da pasta de assemblies .NET do prefixo Wine/Proton, nunca da pasta do jogo, então o DX9 HDR não funciona nele."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.DxvkHdrReShadeXnaGacMessage,
            "O ReShade deste jogo está instalado para XNA, que carrega o d3d9.dll apenas da pasta de assemblies .NET do prefixo, então o DX9 HDR não funciona junto dele e nada foi alterado."),
        (RHI.Platform.Linux.Deployment.ReShadeDeploymentService.RenameMovedByDxvkHdrMessage,
            "O nome da DLL do ReShade não pode ser mudado enquanto o DX9 HDR estiver instalado neste jogo: o DX9 HDR moveu o ReShade para a camada Vulkan dele, que não tem nome de DLL. Remova primeiro o DX9 HDR na linha dele (o ReShade volta a ser d3d9.dll) e depois escolha o nome."),
        // w8/reshade-ini rows
        // w8/renodx rows
        // w8/commit-point rows
        // #204: a ledger write that landed and then reported an error (finished as committed), or a ledger that could not be
        // read again after the error (nothing undone; decided at the next start).
        (RHI.Platform.Linux.Deployment.DeploymentScope.LandedChangePrefix,
            "Concluído; o registro de implantações informou um erro depois que a alteração foi gravada, então nada foi desfeito: "),
        (RHI.Platform.Linux.Deployment.DeploymentScope.LandedRemovalPrefix,
            "Removido; o registro de implantações informou um erro depois que a remoção foi gravada, então nada foi desfeito: "),
        (RHI.Core.Deployment.DeploymentOperation.UndecidedNote,
            "O registro de implantações não pôde ser lido depois do erro, então nada foi desfeito agora; o RHI conclui ou desfaz esta alteração na próxima inicialização."),
        // w8 commit-point review, finding 2: an operation refused while an earlier change to the same game is undecided.
        (RHI.Core.Deployment.DeploymentJournal.EarlierChangeUndecidedMessage,
            "O RHI ainda não concluiu nem desfez uma alteração anterior neste jogo, porque o registro de implantações não pode ser lido. Nada foi alterado. Repare o registro ou reinicie o RHI e tente novamente."),
    ];

    public static string CurrentLanguage { get; private set; } = "en-US";

    // Every UI string, by language: the XAML resource table above plus the code table in UiStrings. Built once; a key
    // defined twice throws here, so a duplicate fails every test instead of silently shadowing a translation.
    private static readonly IReadOnlyDictionary<string, string> AllPtBr = Merge(PtBr, UiStrings.Entries.Select(e => (e.Key, e.PtBr)));
    private static readonly IReadOnlyDictionary<string, string> AllEnUs = Merge(EnUs, UiStrings.Entries.Select(e => (e.Key, e.EnUs)));

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> resources, IEnumerable<(string Key, string Value)> code)
    {
        var all = new Dictionary<string, string>(resources, StringComparer.Ordinal);
        foreach (var (key, value) in code) all.Add(key, value);
        return all;
    }

    /// <summary>Every localisation key (resource and code tables).</summary>
    public static IReadOnlyCollection<string> Keys => AllEnUs.Keys.ToArray();

    /// <summary>Keys present in one language only (must be empty).</summary>
    public static IReadOnlyList<string> KeysMissingAPair
        => AllPtBr.Keys.Except(AllEnUs.Keys).Concat(AllEnUs.Keys.Except(AllPtBr.Keys)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    public static string Text(string key) => TextFor(CurrentLanguage, key);

    public static string TextFor(string? language, string key)
    {
        var table = RHI.Platform.Linux.Settings.LinuxAppSettings.NormalizeUiLanguage(language) == "pt-BR" ? AllPtBr : AllEnUs;
        return table.TryGetValue(key, out var value) ? value : key;
    }

    public static string Format(string key, params object?[] args)
        => string.Format(Culture(CurrentLanguage), Text(key), args);

    public static string FormatFor(string? language, string key, params object?[] args)
        => string.Format(Culture(language), TextFor(language, key), args);

    /// <summary>Numbers and dates follow the chosen UI language, not the system locale.</summary>
    private static CultureInfo Culture(string? language)
        => CultureInfo.GetCultureInfo(RHI.Platform.Linux.Settings.LinuxAppSettings.NormalizeUiLanguage(language));

    /// <summary>Update All status line built from the result's counts, so it never depends on parsing English text.</summary>
    public static string UpdateAllSummary(RHI.Platform.Linux.Deployment.LinuxUpdateAllResult result)
        => UpdateAllSummaryFor(CurrentLanguage, result);

    public static string UpdateAllSummaryFor(string? language, RHI.Platform.Linux.Deployment.LinuxUpdateAllResult result)
    {
        if (result.Items.Count == 0) return TextFor(language, "Ui.UpdateAllNone");
        var text = string.Format(CultureInfo.InvariantCulture, TextFor(language, "Ui.UpdateAllSummary"),
            result.ChangedCount, result.CurrentCount, result.DeferredCount, result.FailedCount);
        if (result.UncheckedCount > 0)
            text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Ui.UpdateAllUnchecked"), result.UncheckedCount);
        // w3/parity (#96): held by NR Cost Scaler; #170: add-ons turned off for ReShade No Addons, counted apart.
        var heldOff = result.Items.Count(item => item.HeldBy == RHI.Platform.Linux.Deployment.LinuxManagedComponentService.ReShadeNoAddonsName);
        if (result.HeldCount - heldOff > 0)
            text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Parity.UpdateAllHeld"), result.HeldCount - heldOff);
        if (heldOff > 0)
            text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Parity.UpdateAllHeldNoAddons"), heldOff);
        if (result.ExcludedCount - result.ExcludedUnreadableCount > 0) // w5/update-policy (#178)
            text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Upd.UpdateAllExcluded"), result.ExcludedCount - result.ExcludedUnreadableCount);
        if (result.ExcludedUnreadableCount > 0) // #178: the inclusion file cannot be read, so everything was left out
            text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Upd.UpdateAllExcludedUnreadable"), result.ExcludedUnreadableCount);
        text += ".";
        // Name the first failure and its reason (the status-bar tooltip shows the whole line); per-item detail is #113.
        var failed = result.FailedItems;
        if (failed.Count == 0) return text;
        var first = failed[0];
        var reason = first.Blocked
            ? TextFor(language, "Ui.UpdateAllBlocked")
            : PresentFor(language, first.Message).TrimEnd().TrimEnd('.');
        text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Ui.UpdateAllFirstFailure"),
            first.GameName, RHI.Platform.Linux.Deployment.LinuxManagedComponentService.DisplayDependencyName(first.Component), reason);
        if (failed.Count > 1)
            text += string.Format(CultureInfo.InvariantCulture, TextFor(language, "Ui.UpdateAllMoreFailures"), failed.Count - 1);
        return text + ".";
    }

    /// <summary>The Shaders/Add-ons manager's success line; keeps ReShade's "cached copy used" note (#132).</summary>
    public static string ShaderAddonAppliedFor(string? language, int shaderPacks, int addons, bool reShadeFromCache)
        => string.Format(CultureInfo.InvariantCulture, TextFor(language, "Ui.ShaderAddonApplied"), shaderPacks, addons)
           + (reShadeFromCache ? TextFor(language, "Ui.ReShadeCachedCopyUsed") : "");

    public static bool HasKeyParity => KeysMissingAPair.Count == 0;

    public static string Present(string? value)
        => PresentFor(CurrentLanguage, value);

    public static string PresentFor(string? language, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var normalized = RHI.Platform.Linux.Settings.LinuxAppSettings.NormalizeUiLanguage(language);
        if (normalized != "pt-BR")
            value = TranslatePortugueseOnly(value);
        foreach (var pair in ExactPresentationPairs)
        {
            if (normalized == "pt-BR" && value.Equals(pair.En, StringComparison.Ordinal)) return pair.Pt;
            if (normalized != "pt-BR" && value.Equals(pair.Pt, StringComparison.Ordinal)) return pair.En;
        }

        var result = value;
        if (normalized == "pt-BR")
        {
            foreach (var pair in PresentationPairs.OrderByDescending(pair => pair.En.Length))
                result = result.Replace(pair.En, pair.Pt, StringComparison.Ordinal);
        }
        else
        {
            // PT→EN runs over English text that may hold a game name or path: a fragment is rewritten only where it
            // stands as words of its own, never glued inside a path segment or a word (Refs #149).
            foreach (var pair in PresentationPairs.OrderByDescending(pair => pair.Pt.Length))
                result = ReplaceAnchored(result, pair.Pt, pair.En);
        }

        // Never leak a clearly opposite-language backend sentence into the selected UI.
        // Known messages above retain their detailed translation; old/unmapped platform text
        // fails closed to a localized generic presentation instead of producing mixed UI.
        if (LooksLikeOppositeLanguage(normalized, result))
        {
            // The real text is not lost: it goes to the error log once, so "see the logs" is true.
            LogUntranslatedOnce(normalized, value);
            return UnlocalizedFallbackFor(normalized);
        }

        return result;
    }

    /// <summary>
    /// Replaces each occurrence of <paramref name="fragment"/> that is not glued to what surrounds it: when the fragment
    /// starts with a letter or digit, the character before it must not be a letter, digit or path separator ("/", "\"),
    /// and when it ends with one, the character after it must not be a letter or digit. A folder named after a message
    /// ("/mnt/Jogos/A pasta não existe: x") is therefore left as it is.
    /// </summary>
    internal static string ReplaceAnchored(string text, string fragment, string replacement)
    {
        if (fragment.Length == 0 || !text.Contains(fragment, StringComparison.Ordinal)) return text;
        var checkStart = char.IsLetterOrDigit(fragment[0]);
        var checkEnd = char.IsLetterOrDigit(fragment[^1]);
        var builder = new StringBuilder(text.Length);
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(fragment, from, StringComparison.Ordinal);
            if (at < 0) break;
            var end = at + fragment.Length;
            var gluedBefore = checkStart && at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '/' or '\\');
            var gluedAfter = checkEnd && end < text.Length && char.IsLetterOrDigit(text[end]);
            builder.Append(text, from, at - from);
            builder.Append(gluedBefore || gluedAfter ? fragment : replacement);
            from = end;
        }
        builder.Append(text, from, text.Length - from);
        return builder.ToString();
    }

    /// <summary>
    /// What <see cref="PresentFor"/> shows for a platform message with no presentation in <paramref name="language"/>:
    /// a generic line naming the app log, where <see cref="LogUntranslatedOnce"/> wrote the original text.
    /// </summary>
    public static string UnlocalizedFallbackFor(string? language)
        => FormatFor(language, "Ui.UnlocalizedPlatformMessage", RHI.Desktop.Diagnostics.UiExceptionGuard.Log.FilePath);

    /// <summary>English marker words left in <paramref name="value"/> (tests: a PT-BR presentation must have none).</summary>
    internal static IReadOnlyList<string> EnglishMarkersIn(string value)
        => Tokens(ProductNames.Aggregate(value, (text, name) => text.Replace(name, " ", StringComparison.Ordinal)).Replace('\'', ' '))
            .Where(EnglishMarkers.Contains).ToArray();

    // Product names Portuguese text keeps in English ("Pastas do Xbox / Game Pass"): not untranslated words.
    private static readonly string[] ProductNames = ["Game Pass"];

    private sealed record PortugueseOnlyTemplate(string Anchor, Regex Pattern, string Replacement, int LiteralLength);

    // A template matches only a whole message or sentence (review m3): it starts at the start of the text, a line, or
    // after a sentence/clause break (". ", ": ", "; ", "! ", "? ", "— "), and ends at the end of the text or before
    // whitespace. A game name or path that merely contains a template's words ("Jogo 'Atualização disponível: …'") is
    // therefore left alone. The input can carry exception text, so every match is time-limited.
    private const string MessageStart = @"(?<=^|\n|[.:;!?—–]\s)";
    private const string MessageEnd = @"(?=$|\s)";
    internal static readonly TimeSpan TemplateMatchTimeout = TimeSpan.FromMilliseconds(200);

    // Longest literal text first, so a message that embeds another (": {2}") is matched by its own row before the
    // embedded message's row rewrites the inside.
    private static readonly PortugueseOnlyTemplate[] PortugueseOnlyTemplates = PlatformMessageTranslations.PortugueseOnly
        .Select(BuildTemplate)
        .OrderByDescending(t => t.LiteralLength)
        .ToArray();

    /// <summary>Match timeouts of the PT-only templates (tests: none may run unbounded).</summary>
    internal static IEnumerable<TimeSpan> PortugueseOnlyTemplateTimeouts => PortugueseOnlyTemplates.Select(t => t.Pattern.MatchTimeout);

    private static PortugueseOnlyTemplate BuildTemplate((string PtBr, string EnUs) row)
    {
        var parts = Regex.Split(row.PtBr, @"\{(\d+)\}");
        var pattern = new StringBuilder(MessageStart);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0) { pattern.Append(Regex.Escape(parts[i])); continue; }
            var last = i == parts.Length - 2 && parts[^1].Length == 0;
            pattern.Append("(?<g").Append(parts[i]).Append(last ? ">.+)" : ">.+?)");
        }
        pattern.Append(MessageEnd);
        var literals = parts.Where((_, i) => i % 2 == 0).ToArray();
        var replacement = Regex.Replace(row.EnUs.Replace("$", "$$"), @"\{(\d+)\}", "${g$1}");
        return new PortugueseOnlyTemplate(literals.OrderByDescending(p => p.Length).First(),
            new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TemplateMatchTimeout),
            replacement, literals.Sum(p => p.Length));
    }

    private static string TranslatePortugueseOnly(string value)
    {
        // A second pass translates a message nested in a hole of the same row (one pass never re-reads its own output).
        for (var pass = 0; pass < 3; pass++)
        {
            var before = value;
            foreach (var template in PortugueseOnlyTemplates)
            {
                if (!value.Contains(template.Anchor, StringComparison.Ordinal)) continue;
                try { value = template.Pattern.Replace(value, template.Replacement); }
                catch (RegexMatchTimeoutException)
                {
                    // Leave the text as it is; the opposite-language check below still keeps PT prose out of the UI.
                    RHI.Desktop.Diagnostics.UiExceptionGuard.Log.TryWrite("translation-timeout", template.Pattern.ToString());
                }
            }
            if (ReferenceEquals(before, value) || before == value) break;
        }
        return value;
    }

    private static readonly HashSet<string> LoggedUntranslated = new(StringComparer.Ordinal);

    private static void LogUntranslatedOnce(string language, string value)
    {
        lock (LoggedUntranslated)
        {
            if (LoggedUntranslated.Count >= 256 || !LoggedUntranslated.Add(value)) return;
        }
        RHI.Desktop.Diagnostics.UiExceptionGuard.Log.TryWrite($"untranslated-message ({language})", value);
    }

    private static bool LooksLikeOppositeLanguage(string language, string value)
    {
        if (value.Length < 24) return false;
        var tokens = Tokens(value);
        if (tokens.Length < 4) return false;

        var english = CountMarkers(tokens, EnglishMarkers);
        var portuguese = CountMarkers(tokens, PortugueseMarkers);
        return language == "pt-BR"
            ? english >= 3 && english > portuguese + 1
            : portuguese >= 3 && portuguese > english + 1;
    }

    private static string[] Tokens(string value)
        => value.Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '—', '_' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int CountMarkers(IEnumerable<string> tokens, HashSet<string> markers)
        => tokens.Count(token => markers.Contains(token));

    private static readonly HashSet<string> EnglishMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "this", "that", "is", "are", "was", "were", "not", "no", "before", "after",
        "first", "game", "games", "directory", "file", "files", "detected", "managed", "requires",
        "install", "installed", "remove", "removed", "update", "failed", "available", "missing",
        "prefix", "executable", "target", "external", "preserve", "preserved", "restore", "restored",
        "cannot", "could", "will", "only", "with", "without", "from", "into", "current"
    };

    private static readonly HashSet<string> PortugueseMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "o", "a", "os", "as", "este", "esta", "isso", "não", "antes", "depois", "primeiro",
        "jogo", "jogos", "diretório", "arquivo", "arquivos", "detectado", "detectada", "gerenciado",
        "gerenciada", "exige", "instalar", "instalado", "instalada", "remover", "removido", "removida",
        "atualizar", "atualização", "falha", "disponível", "ausente", "prefixo", "executável", "alvo",
        "externo", "externa", "preservar", "preservado", "preservada", "restaurar", "restaurado",
        "restaurada", "somente", "com", "sem", "para", "atual"
    };

    public static void Apply(string? language)
    {
        CurrentLanguage = RHI.Platform.Linux.Settings.LinuxAppSettings.NormalizeUiLanguage(language);
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        var table = CurrentLanguage == "pt-BR" ? PtBr : EnUs;
        foreach (var item in table)
            resources[item.Key] = item.Value;
    }
}
