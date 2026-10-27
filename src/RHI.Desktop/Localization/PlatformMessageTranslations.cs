namespace RHI.Desktop.Localization;

/// <summary>
/// English for platform messages that the Linux services still emit in Portuguese only (UI-11 #79, #143). The
/// presentation boundary (<see cref="UiLocalizationService.PresentFor"/>) rewrites them for any non-PT UI; PT-BR shows
/// the service text as is. <c>{n}</c> marks interpolated values, carried over unchanged (a nested message in a value is
/// translated by its own row). New platform messages should be written in English with a PT-BR presentation pair instead.
/// </summary>
internal static class PlatformMessageTranslations
{
    internal static readonly (string PtBr, string EnUs)[] PortugueseOnly =
    [
        ("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial .zip ou .7z. Selecione-o no campo de pacote consumidor antes de instalar.", "DLSS5 Feeder Full with Deep Fried Chicken needs the official .zip or .7z package. Select it in the consumer package field before installing."),
        ("O pacote consumidor do Deep Fried Chicken deve ser um arquivo oficial .zip ou .7z.", "The Deep Fried Chicken consumer package must be an official .zip or .7z file."),
        ("Consumidor neural conflitante detectado: {0}. Apenas um consumidor neural pode controlar o processo. Remova explicitamente o add-on conflitante primeiro.", "Conflicting neural consumer detected: {0}. Only one neural consumer can control the process. Remove the conflicting add-on explicitly first."),
        ("RTX 20 FG Unlock legado já está instalado. Remova e reinstale para trocar a DLL proxy.", "Legacy RTX 20 FG Unlock is already installed. Remove and reinstall it to change the proxy DLL."),
        ("RTX 30 agora usa Universal MFG. Abra as configurações do Universal MFG e selecione RTX 30 Series.", "RTX 30 now uses Universal MFG. Open the Universal MFG settings and select RTX 30 Series."),
        ("Geração de GPU inválida: {0}.", "Invalid GPU generation: {0}."),
        ("Nome de proxy não suportado pelo RTX 20 FG Unlock legado: {0}.", "Proxy name not supported by legacy RTX 20 FG Unlock: {0}."),
        ("RTX 20 FG Unlock legado é um proxy DLL Windows e não se aplica a jogos Linux nativos.", "Legacy RTX 20 FG Unlock is a Windows proxy DLL and does not apply to native Linux games."),
        ("Selecione primeiro o executável real do jogo em Editar jogo.", "First select the real game executable in Edit game."),
        ("RTX 20 FG Unlock legado exige um executável Windows x64.", "Legacy RTX 20 FG Unlock needs a Windows x64 executable."),
        ("O caminho experimental RTX 20 / SM75 está habilitado somente para jogos DirectX 12.", "The experimental RTX 20 / SM75 path is enabled only for DirectX 12 games."),
        ("Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o RTX 20 FG Unlock legado.", "Start the game once to create/initialise the Wine/Proton prefix before installing legacy RTX 20 FG Unlock."),
        ("O diretório do executável alvo não pôde ser resolvido.", "The target executable's directory could not be resolved."),
        ("Compatível com o caminho experimental: Windows x64 + DirectX 12 + prefixo Wine/Proton inicializado.", "Compatible with the experimental path: Windows x64 + DirectX 12 + initialised Wine/Proton prefix."),
        ("RTX 20 FG Unlock legado já é gerenciado pelo RHI para este jogo.", "Legacy RTX 20 FG Unlock is already managed by RHI for this game."),
        ("RTX 30 agora usa Universal MFG. A rota SM86 antiga foi aposentada para novas instalações; use Universal MFG com RTX 30 Series.", "RTX 30 now uses Universal MFG. The old SM86 route was retired for new installs; use Universal MFG with RTX 30 Series."),
        ("MFG Ada Unlock já está ativo. Remova-o antes de instalar o RTX 20 FG Unlock legado; os dois métodos de desbloqueio de Frame Generation não devem compartilhar o mesmo processo.", "MFG Ada Unlock is already active. Remove it before installing legacy RTX 20 FG Unlock; the two Frame Generation unlock methods must not share the same process."),
        ("Universal MFG já está ativo. Remova-o antes de instalar o RTX 20 FG Unlock legado; os dois métodos não devem disputar o mesmo pipeline de Frame Generation.", "Universal MFG is already active. Remove it before installing legacy RTX 20 FG Unlock; the two methods must not compete for the same Frame Generation pipeline."),
        ("ShortFuse já está ativo e gerencia a pilha DLSS/Frame Generation deste jogo. Remova-o antes de usar o RTX 20 FG Unlock legado para evitar duas autoridades sobre o mesmo runtime.", "ShortFuse is already active and manages this game's DLSS/Frame Generation stack. Remove it before using legacy RTX 20 FG Unlock to avoid two authorities over the same runtime."),
        ("Proxy não suportado: {0}.", "Proxy not supported: {0}."),
        ("{0} já pertence a outro componente gerenciado pelo RHI. Escolha outro proxy no botão ⚙.", "{0} already belongs to another RHI-managed component. Choose another proxy with the ⚙ button."),
        ("{0} já existe e não pertence ao RTX 20 FG Unlock legado gerenciado pelo RHI. Escolha outro proxy; o RHI não sobrescreve uma DLL externa.", "{0} already exists and does not belong to the RHI-managed legacy RTX 20 FG Unlock. Choose another proxy; RHI does not overwrite an external DLL."),
        ("{0} já existe e não é gerenciado pelo RHI. A instalação foi bloqueada para não adotar/sobrescrever uma configuração externa.", "{0} already exists and is not managed by RHI. The install was blocked so an external configuration is not adopted or overwritten."),
        ("RTX 20 FG Unlock legado {0} instalado como {1} · {2} / {3}. O suporte sob VKD3D-Proton permanece experimental e deve ser confirmado em jogo real.", "Legacy RTX 20 FG Unlock {0} installed as {1} · {2} / {3}. Support under VKD3D-Proton remains experimental and must be confirmed in a real game."),
        ("RTX 20 FG Unlock legado não é gerenciado pelo RHI para este jogo. A remoção externa permanece protegida.", "Legacy RTX 20 FG Unlock is not managed by RHI for this game. External removal stays protected."),
        ("Registro do RTX 20 FG Unlock legado não possui o nome da DLL proxy; remoção bloqueada para proteger arquivos externos.", "The legacy RTX 20 FG Unlock record has no proxy DLL name; removal blocked to protect external files."),
        ("Registro do RTX 20 FG Unlock legado perdeu o hash da DLL gerenciada; remoção bloqueada.", "The legacy RTX 20 FG Unlock record lost the managed DLL's hash; removal blocked."),
        ("A DLL gerenciada {0} foi modificada fora do RHI. Remoção bloqueada antes de alterar o override Wine.", "The managed DLL {0} was modified outside RHI. Removal blocked before changing the Wine override."),
        ("RTX 20 FG Unlock legado removido e override Wine restaurado. {0} foi preservado porque foi alterado pelo usuário/outro mod.", "Legacy RTX 20 FG Unlock removed and Wine override restored. {0} was kept because it was changed by the user/another mod."),
        ("Última versão upstream: {0} ({1}).", "Latest upstream version: {0} ({1})."),
        ("Atualização disponível: {0} → {1} ({2}).", "Update available: {0} → {1} ({2})."),
        ("RTX 20 FG Unlock legado {0} está no snapshot atual.", "Legacy RTX 20 FG Unlock {0} is on the current snapshot."),
        ("RTX 20 FG Unlock legado não está instalado pelo RHI.", "Legacy RTX 20 FG Unlock is not installed by RHI."),
        ("Registro do RTX 20 FG Unlock legado está sem proxy; remova e reinstale em vez de adotar o estado.", "The legacy RTX 20 FG Unlock record has no proxy; remove and reinstall instead of adopting the state."),
        ("A DLL gerenciada do RTX 20 FG Unlock legado está ausente ou sem hash. Remova/reinstale para recuperar com segurança.", "The managed legacy RTX 20 FG Unlock DLL is missing or has no hash. Remove/reinstall to recover safely."),
        ("{0} foi removido externamente. Atualização bloqueada; use remover/reinstalar para não recriar configuração apagada pelo usuário.", "{0} was removed externally. Update blocked; use remove/reinstall so configuration deleted by the user is not recreated."),
        ("A DLL gerenciada {0} foi modificada fora do RHI. Atualização bloqueada.", "The managed DLL {0} was modified outside RHI. Update blocked."),
        ("RTX 20 FG Unlock legado {0} já usa o snapshot upstream atual.", "Legacy RTX 20 FG Unlock {0} already uses the current upstream snapshot."),
        ("RTX 20 FG Unlock legado atualizado para {0} ({1}); {2} do usuário foi preservado.", "Legacy RTX 20 FG Unlock updated to {0} ({1}); the user's {2} was kept."),
        ("Ativado pela regra de força (force) do manifesto do RHI.", "Enabled by the RHI manifest's force rule."),
        ("O DOF Fix é habilitado apenas para títulos explicitamente listados ou jogos Unreal Engine 5.0–5.6 detectados.", "DOF Fix is enabled only for explicitly listed titles or detected Unreal Engine 5.0–5.6 games."),
        ("Alvo de modificações não definido — o launcher foi preservado, mas o executável real do jogo ainda precisa ser confirmado em Editar jogo.", "Modification target not set — the launcher was kept, but the real game executable still has to be confirmed in Edit game."),
        ("Alvo de modificações não definido. O executável de inicialização/launcher foi preservado, mas o RHI não instalará, atualizará ou removerá arquivos locais de componentes até você selecionar o executável real do jogo em Editar jogo.", "Modification target not set. The startup/launcher executable was kept, but RHI will not install, update or remove local component files until you select the real game executable in Edit game."),
        (" ReShade será instalado automaticamente antes do DOF Fix.", " ReShade will be installed automatically before DOF Fix."),
        (" RHI Linux instala o add-on upstream renodx-universal_ue-dof-fix.addon64 com verificação de integridade e restauração segura.", " RHI Linux installs the upstream add-on renodx-universal_ue-dof-fix.addon64 with integrity checks and safe restore."),
        ("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar MFG Ada Unlock; os métodos de desbloqueio de Frame Generation são mutuamente exclusivos no RHI-Linux.", "Legacy RTX 20 FG Unlock is already active. Remove it before installing MFG Ada Unlock; the Frame Generation unlock methods are mutually exclusive in RHI-Linux."),
        ("Universal MFG já está ativo. Remova-o antes de instalar MFG Ada Unlock; mantenha uma única autoridade de Frame Generation.", "Universal MFG is already active. Remove it before installing MFG Ada Unlock; keep a single Frame Generation authority."),
        ("Add-on por jogo. Exige Geração de Quadros DLSS fornecida pelo jogo via Streamline e uma nvngx_dlssg.dll moderna compatível.", "Per-game add-on. Needs DLSS Frame Generation provided by the game through Streamline and a compatible modern nvngx_dlssg.dll."),
        ("O método DX11 Bridge é destinado a jogos DX11/Vulkan com DLSS nativo. Este alvo não é DX11/Vulkan.", "The DX11 Bridge method is meant for DX11/Vulkan games with native DLSS. This target is not DX11/Vulkan."),
        ("Troca de método: instalar o DX11 Bridge removerá transacionalmente primeiro o método ShortFuse/Feeder atualmente gerenciado pelo RHI.", "Method switch: installing DX11 Bridge first transactionally removes the ShortFuse/Feeder method RHI currently manages."),
        ("Troca de método: instalar o Feeder Full removerá transacionalmente primeiro o método ShortFuse/DX11 Bridge atualmente gerenciado pelo RHI.", "Method switch: installing Feeder Full first transactionally removes the ShortFuse/DX11 Bridge method RHI currently manages."),
        ("Universal MFG já está ativo. Remova-o antes de instalar RTX 20 FG Unlock legado.", "Universal MFG is already active. Remove it before installing legacy RTX 20 FG Unlock."),
        ("MFG Ada Unlock já está ativo. Remova-o antes de instalar RTX 20 FG Unlock legado.", "MFG Ada Unlock is already active. Remove it before installing legacy RTX 20 FG Unlock."),
        ("ShortFuse já gerencia a pilha DLSS/Frame Generation. Remova-o antes de usar o RTX 20 FG Unlock legado.", "ShortFuse already manages the DLSS/Frame Generation stack. Remove it before using legacy RTX 20 FG Unlock."),
        ("Legado/Experimental sob Proton/VKD3D: RTX 20 / SM75, proxy {0}. RTX 30 migrou para Universal MFG. Exige jogo x64 DirectX 12; o componente RTX 20 usa uma fonte upstream fixada para manter comportamento reproduzível.", "Legacy/Experimental under Proton/VKD3D: RTX 20 / SM75, proxy {0}. RTX 30 moved to Universal MFG. Needs an x64 DirectX 12 game; the RTX 20 component uses a pinned upstream source to keep behaviour reproducible."),
        ("FG Unlock legado gerenciado pelo RHI · {0} · {1}. Registros RTX 30 antigos continuam removíveis/atualizáveis, mas novas instalações RTX 30 usam Universal MFG.", "RHI-managed legacy FG Unlock · {0} · {1}. Old RTX 30 records can still be removed/updated, but new RTX 30 installs use Universal MFG."),
        ("Standalone, independente do OptiScaler · proxy {0}. O RHI protege slots existentes, aplica override Wine por jogo e valida o pacote x64; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.", "Standalone, independent of OptiScaler · proxy {0}. RHI protects existing slots, applies a per-game Wine override and validates the x64 package; compatibility under Wine/Proton must be confirmed in the game."),
        ("DLSS Enabler Standalone gerenciado pelo RHI · proxy {0}. Instalação independente do OptiScaler; remoção/update são protegidos por hash e restauram o override Wine anterior.", "RHI-managed DLSS Enabler Standalone · proxy {0}. Installed independently of OptiScaler; removal/update are hash-protected and restore the previous Wine override."),
        ("MFG Ada Unlock já está ativo. Remova-o antes de instalar Universal MFG.", "MFG Ada Unlock is already active. Remove it before installing Universal MFG."),
        ("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar Universal MFG.", "Legacy RTX 20 FG Unlock is already active. Remove it before installing Universal MFG."),
        ("ShortFuse já gerencia Frame Generation. Remova-o antes de instalar Universal MFG.", "ShortFuse already manages Frame Generation. Remove it before installing Universal MFG."),
        ("Universal MFG Experimental · RTX 30 · DirectX 12 · proxy {0}. O suporte RTX 30 é experimental upstream; o RHI valida integridade/arquitetura, protege arquivos existentes e Backspace abre o menu.", "Universal MFG Experimental · RTX 30 · DirectX 12 · proxy {0}. RTX 30 support is experimental upstream; RHI validates integrity/architecture, protects existing files, and Backspace opens the menu."),
        ("Universal MFG Experimental · RTX 40 · proxy {0}. DirectX 12 é o caminho principal e Vulkan permanece experimental; o RHI valida integridade/arquitetura e protege arquivos existentes.", "Universal MFG Experimental · RTX 40 · proxy {0}. DirectX 12 is the main path and Vulkan stays experimental; RHI validates integrity/architecture and protects existing files."),
        ("Universal MFG Experimental gerenciado pelo RHI · {0} · proxy {1}. Update/removal são protegidos por hash e preservam o override Wine anterior.", "RHI-managed Universal MFG Experimental · {0} · proxy {1}. Update/removal are hash-protected and keep the previous Wine override."),
        ("Universal MFG Experimental já está ativo. Remova-o antes de instalar ShortFuse.", "Universal MFG Experimental is already active. Remove it before installing ShortFuse."),
        ("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar ShortFuse.", "Legacy RTX 20 FG Unlock is already active. Remove it before installing ShortFuse."),
        ("ShortFuse: pilha DLSS/Streamline transacional. A configuração automática ASI é opcional; quando ativada usa ReShade64.asi + prioridade UAL winmm→version→dinput8 + HookStreamline/HookDirectX.", "ShortFuse: transactional DLSS/Streamline stack. Automatic ASI configuration is optional; when enabled it uses ReShade64.asi + UAL priority winmm→version→dinput8 + HookStreamline/HookDirectX."),
        ("O OptiScaler suporta mais APIs, mas o deployment automático de proxy é habilitado apenas para jogos DX11/DX12 detectados. Vulkan exige primeiro uma escolha explícita de loader.", "OptiScaler supports more APIs, but automatic proxy deployment is enabled only for detected DX11/DX12 games. Vulkan first needs an explicit loader choice."),
        ("Alvo de modificações não definido. Use Editar jogo para selecionar o executável real do jogo; o executável do launcher nunca é usado como destino de patches.", "Modification target not set. Use Edit game to select the real game executable; the launcher executable is never used as a patch target."),
        ("Alvo de modificações não definido. Atualizações locais estão bloqueadas até Editar jogo confirmar o executável real do jogo.", "Modification target not set. Local updates are blocked until Edit game confirms the real game executable."),
        ("A dependência gerenciada {0} possui registro de ownership, mas seu payload não está presente. Repare ou remova esse estado antes de instalar {1}.", "The managed dependency {0} has an ownership record, but its payload is missing. Repair or remove that state before installing {1}."),
        ("Instalação cancelada durante a resolução automática de dependências.", "Install cancelled while resolving dependencies automatically."),
        ("Falha inesperada durante a resolução automática de dependências: {0}", "Unexpected failure while resolving dependencies automatically: {0}"),
        ("Instalação cancelada antes de concluir o componente solicitado.", "Install cancelled before the requested component was finished."),
        ("Dependências instaladas automaticamente ({0}). {1}", "Dependencies installed automatically ({0}). {1}"),
        ("A dependência gerenciada {0} ficou inconsistente durante a resolução automática.", "The managed dependency {0} became inconsistent while resolving dependencies automatically."),
        ("Não foi possível instalar automaticamente a dependência {0} exigida por {1}: {2}", "Could not automatically install the dependency {0} required by {1}: {2}"),
        ("As dependências instaladas nesta tentativa foram revertidas.", "The dependencies installed in this attempt were rolled back."),
        ("Rollback parcial das dependências: {0}", "Partial rollback of the dependencies: {0}"),
        ("Display Commander já está ativo. ReLimiter e Display Commander são mutuamente exclusivos.", "Display Commander is already active. ReLimiter and Display Commander are mutually exclusive."),
        ("ReLimiter já está ativo. Display Commander e ReLimiter são mutuamente exclusivos.", "ReLimiter is already active. Display Commander and ReLimiter are mutually exclusive."),
        ("Luma Framework já está ativo e este jogo não permite coexistência RenoDX/Luma pelo manifesto. Nenhuma dependência foi alterada.", "Luma Framework is already active and this game's manifest does not allow RenoDX/Luma together. No dependency was changed."),
        ("RenoDX HDR já está ativo e este jogo não permite coexistência RenoDX/Luma pelo manifesto. Nenhuma dependência foi alterada.", "RenoDX HDR is already active and this game's manifest does not allow RenoDX/Luma together. No dependency was changed."),
        ("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar MFG Ada Unlock.", "Legacy RTX 20 FG Unlock is already active. Remove it before installing MFG Ada Unlock."),
        ("Universal MFG já está ativo. Remova-o antes de instalar MFG Ada Unlock.", "Universal MFG is already active. Remove it before installing MFG Ada Unlock."),
        ("OptiScaler já está ativo. ShortFuse não reescreve automaticamente essa cadeia ReShade/OptiScaler.", "OptiScaler is already active. ShortFuse does not rewrite that ReShade/OptiScaler chain automatically."),
        ("Não foi possível instalar automaticamente D3DCompiler_47, dependência de {0}: {1}", "Could not automatically install D3DCompiler_47, a dependency of {0}: {1}"),
        ("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial .zip ou .7z antes que dependências sejam alteradas.", "DLSS5 Feeder Full with Deep Fried Chicken needs the official .zip or .7z package before any dependency is changed."),
        ("{0} não participa do fluxo Atualizar tudo.", "{0} does not take part in Update All."),
        ("O suporte de proxy do OptiScaler é habilitado automaticamente apenas para jogos DirectX 11/12 detectados. Vulkan exige uma escolha explícita de loader/proxy antes de poder ser habilitado com segurança.", "OptiScaler proxy support is enabled automatically only for detected DirectX 11/12 games. Vulkan needs an explicit loader/proxy choice before it can be enabled safely."),
        ("RE Framework está disponível para títulos RE Engine detectados.", "RE Framework is available for detected RE Engine titles."),
        ("Nenhum perfil RenoDX específico ou fallback genérico seguro foi encontrado para este jogo.", "No specific RenoDX profile or safe generic fallback was found for this game."),
        ("A chave da configuração de compatibilidade do RenoDX está vazia.", "The RenoDX compatibility setting key is empty."),
        ("O ReShade.ini ainda não contém uma seção [renodx]. Execute o jogo uma vez com o RenoDX instalado.", "ReShade.ini does not contain a [renodx] section yet. Run the game once with RenoDX installed."),
        ("O caminho do ReShade.ini não pôde ser resolvido após salvar a configuração do RenoDX.", "The ReShade.ini path could not be resolved after saving the RenoDX setting."),
        ("Configuração do RenoDX salva: {0}={1} → {2}", "RenoDX setting saved: {0}={1} → {2}"),
        ("Não foi possível salvar a configuração de compatibilidade do RenoDX: {0}", "Could not save the RenoDX compatibility setting: {0}"),
        ("Universal MFG já está instalado. Remova e reinstale para trocar a geração da GPU ou a DLL proxy.", "Universal MFG is already installed. Remove and reinstall it to change the GPU generation or the proxy DLL."),
        ("Geração de GPU não suportada pelo Universal MFG: {0}.", "GPU generation not supported by Universal MFG: {0}."),
        ("DLL proxy não suportada pelo Universal MFG: {0}.", "Proxy DLL not supported by Universal MFG: {0}."),
        ("Os proxies Bink exigem chaining da DLL original (*Hooked.dll). Esse fluxo ainda não é automatizado no RHI-Linux; escolha outro proxy.", "Bink proxies need the original DLL chained (*Hooked.dll). That flow is not automated in RHI-Linux yet; choose another proxy."),
        ("Universal MFG é um proxy DLL Windows e não se aplica a jogos Linux nativos.", "Universal MFG is a Windows proxy DLL and does not apply to native Linux games."),
        ("Universal MFG exige um executável Windows x64.", "Universal MFG needs a Windows x64 executable."),
        ("Universal MFG em RTX 30 é experimental e exige DirectX 12; Vulkan não é habilitado para RTX 30 neste caminho.", "Universal MFG on RTX 30 is experimental and needs DirectX 12; Vulkan is not enabled for RTX 30 on this path."),
        ("Universal MFG em RTX 40 usa DirectX 12 como caminho principal e Vulkan experimental; DirectX 11 não é elegível.", "Universal MFG on RTX 40 uses DirectX 12 as the main path and Vulkan as experimental; DirectX 11 is not eligible."),
        ("Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o Universal MFG.", "Start the game once to create/initialise the Wine/Proton prefix before installing Universal MFG."),
        ("Universal MFG exige Streamline DLSS Frame Generation funcional: integração nativa do jogo (Streamline + nvngx_dlssg.dll) ou a pilha OptiScaler Nightly/DLSS NR/F5 DLSSNR gerenciada pelo RHI com Streamline + DLSS Enabler e FG Output=DLSSG / FGNvngxReplacement=Arturs.", "Universal MFG needs working Streamline DLSS Frame Generation: the game's native integration (Streamline + nvngx_dlssg.dll) or the RHI-managed OptiScaler Nightly/DLSS NR/F5 DLSSNR stack with Streamline + DLSS Enabler and FG Output=DLSSG / FGNvngxReplacement=Arturs."),
        ("Compatível com o caminho experimental Universal MFG: {0} + Streamline DLSS Frame Generation + prefixo Wine/Proton inicializado.", "Compatible with the experimental Universal MFG path: {0} + Streamline DLSS Frame Generation + initialised Wine/Proton prefix."),
        ("Universal MFG já é gerenciado pelo RHI para este jogo.", "Universal MFG is already managed by RHI for this game."),
        ("{0} já está ativo. Remova esse método antes de instalar Universal MFG para manter uma única autoridade de Frame Generation.", "{0} is already active. Remove that method before installing Universal MFG to keep a single Frame Generation authority."),
        ("Proxy não suportado pelo Universal MFG: {0}.", "Proxy not supported by Universal MFG: {0}."),
        ("Proxy Bink requer preservar/renomear a DLL original para *Hooked.dll. O RHI-Linux ainda não automatiza esse chaining; escolha outro proxy.", "A Bink proxy needs the original DLL kept/renamed to *Hooked.dll. RHI-Linux does not automate that chaining yet; choose another proxy."),
        ("{0} já existe e é externo (inclusive considerando variações de maiúsculas/minúsculas). O RHI não sobrescreve nem adota uma DLL proxy existente; escolha outro nome.", "{0} already exists and is external (case variations included). RHI does not overwrite or adopt an existing proxy DLL; choose another name."),
        ("Universal MFG {0} instalado para {1} como {2}. Pressione Backspace no jogo para abrir o menu upstream; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.", "Universal MFG {0} installed for {1} as {2}. Press Backspace in the game to open the upstream menu; compatibility under Wine/Proton must be confirmed in the game."),
        ("Universal MFG não é gerenciado pelo RHI para este jogo. Instalações externas permanecem protegidas.", "Universal MFG is not managed by RHI for this game. External installs stay protected."),
        ("Registro do Universal MFG não possui o nome da DLL proxy; remoção bloqueada.", "The Universal MFG record has no proxy DLL name; removal blocked."),
        ("Registro do Universal MFG perdeu o hash de ownership; remoção bloqueada.", "The Universal MFG record lost its ownership hash; removal blocked."),
        ("Universal MFG removido e o override Wine anterior foi restaurado.", "Universal MFG removed and the previous Wine override restored."),
        ("Último Universal MFG upstream: {0}.", "Latest upstream Universal MFG: {0}."),
        ("Universal MFG {0} está atualizado.", "Universal MFG {0} is up to date."),
        ("Universal MFG não é gerenciado pelo RHI para este jogo.", "Universal MFG is not managed by RHI for this game."),
        ("Registro do Universal MFG não possui proxy; update bloqueado.", "The Universal MFG record has no proxy; update blocked."),
        ("Ownership hash do Universal MFG está ausente; update bloqueado.", "The Universal MFG ownership hash is missing; update blocked."),
        ("A DLL gerenciada do Universal MFG não existe mais; update não recriará um slot perdido silenciosamente.", "The managed Universal MFG DLL no longer exists; the update will not silently recreate a lost slot."),
        ("A DLL gerenciada do Universal MFG foi modificada externamente; update bloqueado.", "The managed Universal MFG DLL was modified externally; update blocked."),
        ("Universal MFG {0} já está atualizado.", "Universal MFG {0} is already up to date."),
        ("DLSS Enabler Standalone já está instalado. Remova e reinstale para trocar a DLL proxy.", "DLSS Enabler Standalone is already installed. Remove and reinstall it to change the proxy DLL."),
        ("DLL proxy não suportada pelo DLSS Enabler Standalone: {0}.", "Proxy DLL not supported by DLSS Enabler Standalone: {0}."),
        ("DLSS Enabler Standalone é um proxy DLL Windows e não se aplica a jogos Linux nativos.", "DLSS Enabler Standalone is a Windows proxy DLL and does not apply to native Linux games."),
        ("O pacote standalone atual do DLSS Enabler é tratado como Windows x64 no caminho Linux.", "The current DLSS Enabler standalone package is treated as Windows x64 on the Linux path."),
        ("Inicie o jogo uma vez para criar/inicializar o prefixo Wine/Proton antes de instalar o DLSS Enabler Standalone.", "Start the game once to create/initialise the Wine/Proton prefix before installing DLSS Enabler Standalone."),
        ("Compatível com o modo standalone: Windows x64 + Patch Target confirmado + prefixo Wine/Proton inicializado.", "Compatible with standalone mode: Windows x64 + confirmed Patch Target + initialised Wine/Proton prefix."),
        ("DLSS Enabler Standalone já é gerenciado pelo RHI para este jogo.", "DLSS Enabler Standalone is already managed by RHI for this game."),
        ("{0} já existe e é externo. O RHI não sobrescreve nem adota uma DLL proxy existente; escolha outro nome.", "{0} already exists and is external. RHI does not overwrite or adopt an existing proxy DLL; choose another name."),
        ("DLSS Enabler Standalone {0} instalado como {1}. O deploy é independente do OptiScaler; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.", "DLSS Enabler Standalone {0} installed as {1}. The deployment is independent of OptiScaler; compatibility under Wine/Proton must be confirmed in the game."),
        ("DLSS Enabler Standalone não é gerenciado pelo RHI para este jogo. Instalações externas permanecem protegidas.", "DLSS Enabler Standalone is not managed by RHI for this game. External installs stay protected."),
        ("Registro do DLSS Enabler Standalone não possui o nome da DLL proxy; remoção bloqueada.", "The DLSS Enabler Standalone record has no proxy DLL name; removal blocked."),
        ("Registro do DLSS Enabler Standalone perdeu o hash de ownership; remoção bloqueada.", "The DLSS Enabler Standalone record lost its ownership hash; removal blocked."),
        ("DLSS Enabler Standalone removido e o override Wine anterior foi restaurado.", "DLSS Enabler Standalone removed and the previous Wine override restored."),
        ("Último DLSS Enabler Standalone upstream: {0}.", "Latest upstream DLSS Enabler Standalone: {0}."),
        ("DLSS Enabler Standalone {0} está atualizado.", "DLSS Enabler Standalone {0} is up to date."),
        ("DLSS Enabler Standalone não é gerenciado pelo RHI para este jogo.", "DLSS Enabler Standalone is not managed by RHI for this game."),
        ("Registro do DLSS Enabler Standalone não possui proxy; update bloqueado.", "The DLSS Enabler Standalone record has no proxy; update blocked."),
        ("Ownership hash do DLSS Enabler Standalone está ausente; update bloqueado.", "The DLSS Enabler Standalone ownership hash is missing; update blocked."),
        ("A DLL gerenciada do DLSS Enabler Standalone não existe mais; update não recriará um slot perdido silenciosamente.", "The managed DLSS Enabler Standalone DLL no longer exists; the update will not silently recreate a lost slot."),
        ("A DLL gerenciada do DLSS Enabler Standalone foi modificada externamente; update bloqueado.", "The managed DLSS Enabler Standalone DLL was modified externally; update blocked."),
        ("DLSS Enabler Standalone {0} já está atualizado.", "DLSS Enabler Standalone {0} is already up to date."),
        ("O manifesto do RHI não contém addonPacks/{0}.", "The RHI manifest has no addonPacks/{0}."),
        ("Não foi possível carregar o manifesto de add-ons do RHI: {0}", "Could not load the RHI add-on manifest: {0}"),
        ("{0} não possui URL da API de releases do GitHub no manifesto do RHI.", "{0} has no GitHub releases API URL in the RHI manifest."),
        ("O alvo {0} está configurado, mas o executável não existe no momento. O RHI não substituirá essa autoridade por outro .exe automaticamente.", "The {0} target is configured, but the executable does not exist right now. RHI will not replace that authority with another .exe automatically."),
        ("Launcher detectado como alvo de inicialização. Selecione o executável real do jogo em Editar jogo para habilitar a detecção e os patches locais.", "Launcher detected as the startup target. Select the real game executable in Edit game to enable detection and local patches."),
        ("O pacote do Deep Fried Chicken deve usar o formato oficial .zip ou .7z.", "The Deep Fried Chicken package must use the official .zip or .7z format."),
        ("Falha ao verificar RTX 20 FG Unlock legado: {0}", "Could not check legacy RTX 20 FG Unlock: {0}"),
        ("Desativado pela regra do manifesto do RHI para jogos ignorados.", "Disabled by the RHI manifest rule for ignored games."),
        ("Add-on DLSS5 DX11 Bridge para jogos DX11/Vulkan com DLSS nativo.", "DLSS5 DX11 Bridge add-on for DX11/Vulkan games with native DLSS."),
        ("DLSS5 Feeder 32-bit: selecione RenoDX DLSS5 para usar addon32 + host64. Deep Fried Chicken permanece restrito ao layout 64-bit comprovado.", "DLSS5 Feeder 32-bit: select RenoDX DLSS5 to use addon32 + host64. Deep Fried Chicken stays limited to the proven 64-bit layout."),
        ("Falha inesperada ao instalar {0}: {1}", "Unexpected failure while installing {0}: {1}"),
        ("Falha ao verificar Universal MFG: {0}", "Could not check Universal MFG: {0}"),
        ("Falha ao verificar DLSS Enabler Standalone: {0}", "Could not check DLSS Enabler Standalone: {0}"),
        ("Atualização disponível: desconhecida → {0}.", "Update available: unknown → {0}."),
        ("Atualização disponível: {0} → {1}.", "Update available: {0} → {1}."),
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        // w8/reshade-ini rows
        // w8/renodx rows
        // w8/commit-point rows
        // w10/dlss-presets rows
        // w10/archives rows
        // w10/journal-holds rows
        // w11/download-idle rows
        // w11/nr-gpu rows
        // w11/nightly-switch rows
        // w11/nr-check rows
        // w12/mfg rows
        ("O arquivo deste componente foi removido fora do RHI, e as atualizações nunca o colocam de volta. Remover limpa o registro do RHI; depois, Instalar o coloca de novo.",
            "This component's file was removed outside RHI, and updates never put it back. Remove clears RHI's record; Install then puts it back."), // #236/#237/#238
        // w12/shaders rows
        // w12/update-all rows
        // w12/github-cache rows
        // w12/luma rows
        // w12/epic rows
        // w12/nr-logs rows
        // w12/pins rows
    ];
}
