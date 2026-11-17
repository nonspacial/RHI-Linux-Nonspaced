// w14/layouts-c: per-game launch arguments (upstream 2.9.0+), the library-wide update check behind Update All's count and the
// Tabs ⚙ under its card, in their own table so lanes adding to UiStrings do not collide with it. Same row shape as UiStrings:
// key, PT-BR, EN. English follows upstream RHI 2.9.4's wording where the feature is the same.
namespace RHI.Desktop.Localization;

internal static class LayoutsCUiStrings
{
    internal static readonly (string Key, string PtBr, string EnUs)[] Entries =
    [
        // Game Overrides › Launch arguments (upstream: the Launch arguments box and Reset launch options)
        ("LcArgs.Label", "Argumentos de inicialização", "Launch arguments"),
        ("LcArgs.Placeholder", "Argumentos de inicialização", "Launch arguments"),
        ("LcArgs.Tip", "Argumentos de linha de comando passados ao jogo quando o RHI o inicia (regras do Windows: aspas agrupam). Salvo ao sair da caixa ou com Enter.", "Command-line arguments passed to the game on launch (Windows rules: quotes group). Saves on focus lost or Enter."),
        ("LcArgs.Reset", "Redefinir opções de inicialização", "Reset launch options"),
        ("LcArgs.ResetTip", "Limpa os argumentos de inicialização deste jogo e o comando de inicialização digitado em Editar jogo (o jogo volta ao comando detectado).", "Clear the launch arguments for this game and the launch command typed in Edit game (the game goes back to its detected command)."),
        ("LcArgs.Saved", "Argumentos de inicialização de {0} salvos: {1}", "Launch arguments saved for {0}: {1}"),
        ("LcArgs.Cleared", "Argumentos de inicialização de {0} removidos.", "Launch arguments cleared for {0}."),
        ("LcArgs.ProblemNul", "Os argumentos de inicialização não podem conter um caractere NUL. Nada foi salvo.", "Launch arguments cannot contain a NUL character. Nothing was saved."),
        ("LcArgs.ProblemLength", "Os argumentos de inicialização podem ter até {0} caracteres. Nada foi salvo.", "Launch arguments are limited to {0} characters. Nothing was saved."),
        ("LcArgs.ResetNothing", "{0} não tinha opções de inicialização para redefinir.", "{0} had no launch options to reset."),
        ("LcArgs.ResetDone", "{0}: opções de inicialização redefinidas ({1}).", "{0}: launch options reset ({1})."),
        ("LcArgs.ResetCommandPart", "comando de inicialização", "launch command"),
        ("LcArgs.ResetArgsPart", "argumentos de inicialização", "launch arguments"),
        ("LcArgs.ResetManualKeepsCommand", "O comando de um jogo adicionado por você fica: é como o RHI o inicia (mude em Editar jogo).", "A game you added keeps its launch command: it is how RHI starts it (change it in Edit game)."),
        ("LcArgs.RouteDirect", "O RHI acrescenta estes argumentos ao programa do jogo quando o inicia.", "RHI adds these to the game's program when it launches the game."),
        ("LcArgs.RouteSteam", "O RHI inicia o jogo pelo Steam com estes argumentos (steam -applaunch). Iniciado pelo próprio Steam, o jogo usa as opções de inicialização do Steam.", "RHI starts the game through Steam with these arguments (steam -applaunch). Started from Steam itself, the game uses the launch options set in Steam."),
        ("LcArgs.RouteSteamLink", "O RHI inicia este jogo pelo link steam://, que não leva argumentos (o programa steam não está no PATH). No Steam, abra Propriedades do jogo → Geral → Opções de inicialização e adicione-os lá.", "RHI starts this game through Steam's steam:// link, which cannot carry arguments (the steam program is not on PATH). In Steam, open the game's Properties → General → Launch Options and add them there."),
        ("LcArgs.RouteSteamShortcut", "O Steam inicia este jogo não-Steam, então o RHI não acrescenta os argumentos. No Steam, abra as Propriedades do atalho → Opções de inicialização e adicione-os lá.", "Steam starts this non-Steam game, so RHI does not add the arguments. In Steam, open the shortcut's Properties → Launch Options and add them there."),
        ("LcArgs.RouteUserCommand", "Este jogo inicia com o comando de inicialização definido em Editar jogo, e o RHI nunca altera um comando digitado por você. Coloque os argumentos nesse comando.", "This game starts with the launch command set in Edit game, and RHI never changes a command you typed. Put the arguments in that command instead."),
        ("LcArgs.RouteHeroic", "O Heroic inicia este jogo, então o RHI não acrescenta os argumentos ao Iniciar. No Heroic, abra as Configurações de “{0}” → Avançado e adicione-os em argumentos do jogo.", "Heroic starts this game, so RHI does not add the arguments on Launch. In Heroic, open the settings of “{0}” → Advanced and add them as game arguments."),
        ("LcArgs.RouteLutris", "O Lutris inicia este jogo, então o RHI não acrescenta os argumentos ao Iniciar. No Lutris, clique com o botão direito em “{0}” → Configurar e adicione-os em Opções do jogo → Argumentos.", "Lutris starts this game, so RHI does not add the arguments on Launch. In Lutris, right-click “{0}” → Configure and add them under Game options → Arguments."),
        ("LcArgs.RouteParentLauncher", "Iniciar abre {0}, onde você inicia o jogo, então o RHI não acrescenta os argumentos. Adicione-os nas opções do jogo em {0}.", "Launch opens {0}, where you start the game, so RHI does not add the arguments. Add them in the game's options in {0}."),
        ("LcArgs.RouteOtherLauncher", "Outro launcher inicia este jogo, então o RHI não acrescenta os argumentos ao Iniciar. Adicione-os nesse launcher.", "Another launcher starts this game, so RHI does not add the arguments on Launch. Add them in that launcher."),
        ("LcArgs.RouteNoCommand", "O RHI ainda não pode iniciar este jogo: defina um comando de inicialização em Editar jogo.", "RHI cannot launch this game yet: set a launch command in Edit game."),
        ("LcArgs.LaunchWith", "Argumentos de inicialização: {0}", "Launch arguments: {0}"),
        ("LcArgs.LaunchWithout", "Argumentos de inicialização não acrescentados.", "Launch arguments not added."),
        ("Mgmt.LaunchArguments", "argumentos de inicialização", "launch arguments"),

        // Tabs › Launch ▾ (upstream: Executable, Launch arguments..., Create desktop shortcut)
        ("LcMenu.Executable", "Executável: {0}", "Executable: {0}"),
        ("LcMenu.ExecutableAuto", "detecção automática", "auto-detect"),
        ("LcMenu.ExecutableTip", "O programa que o RHI inicia ao clicar em Iniciar. Escolha o executável e o comando de inicialização em Editar jogo.", "The program RHI starts when you click Launch. Choose the executable and launch command in Edit game."),
        ("LcMenu.Arguments", "Argumentos de inicialização...", "Launch arguments..."),

        // Display modes (#311): the launch arguments item
        ("DmItem.LaunchArgs", "Argumentos de inicialização", "Launch arguments"),
        ("DmHelp.LaunchArgs", "Argumentos que o RHI passa ao jogo neste modo (em vez dos de Substituições do jogo), por exemplo -fullscreen. Vazio = nenhum argumento neste modo; Limpar = os argumentos do próprio jogo.", "Arguments RHI passes to the game in this mode (instead of the ones in Game Overrides), e.g. -fullscreen. Empty = no arguments in this mode; Clear = the game's own arguments."),
        ("DmWhy.LaunchArgsNotOffered", "o RHI não acrescenta argumentos ao iniciar este jogo (comando digitado por você ou outro launcher)", "RHI does not add arguments when it launches this game (a command you typed, or another launcher)"),

        // Top bar › Update All (upstream: "Update All (N)", else "All up to date")
        ("Top.AllUpToDate", "Tudo atualizado", "All up to date"),
        ("Top.AllUpToDateTip", "O RHI verificou os componentes que instalou em toda a biblioteca e não há atualizações (jogos ocultos ficam de fora).", "RHI checked the components it installed across your library and there are no updates (hidden games are left out)."),
        ("Top.UpdateAllFoundTip", "Há atualizações em {0} jogo(s). Atualizar tudo atualiza os componentes instalados pelo RHI em toda a biblioteca (jogos ocultos ficam de fora).", "{0} game(s) have updates. Update All updates the components RHI installed across your library (hidden games are left out)."),
        ("Top.CheckCutShort", "Limite de requisições do GitHub atingido: {0} jogo(s) não foram verificados. A contagem cobre os jogos verificados; a próxima verificação continua. Um token do GitHub aumenta o limite.", "GitHub's request limit was reached: {0} game(s) were not checked. The count covers the games checked; the next check carries on. A GitHub token raises the limit."),
        ("Top.CheckCutShortStatus", "Verificação de atualizações interrompida pelo limite de requisições do GitHub: {0} jogo(s) não verificados. Um token do GitHub aumenta o limite.", "Update check cut short by GitHub's request limit: {0} game(s) not checked. A GitHub token raises the limit."),
        ("Sb.ActCheckingLibrary", "Verificando atualizações na biblioteca...", "Checking your library for updates..."),

        // Game list: a game with an update (upstream: the orange dot; covers: the purple outline)
        ("Lib.UpdateTip", "Há uma atualização para um componente instalado pelo RHI.", "An update is available for a component RHI installed."),
    ];
}
