namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Ce que la borne affiche au joueur pendant un challenge.
///
/// La langue se resout par couches, et l'ordre compte :
///
///   1. le joueur, quand il est identifie - sa session porte sa langue ;
///   2. la borne, sinon - c'est la langue de la salle, celle qu'un passant
///      comprend le mieux avant de s'etre connecte ;
///   3. l'anglais en dernier recours.
///
/// La borne elle-meme ne change pas de langue quand un joueur arrive :
/// basculer EmulationStation a chaque check-in serait lourd et lent. Seules
/// ces chaines suivent le joueur.
/// </summary>
public static class CabinetAnnounceText
{
    /// <summary>Les langues servies. Toute autre valeur retombe sur l'anglais.</summary>
    private static readonly string[] Supported = ["en", "fr", "es", "ja", "zh", "ko"];

    /// <summary>
    /// Ramene un code quelconque - « fr », « fr_FR », « fr-FR », « FRANCAIS » -
    /// a l'une des six langues servies.
    /// </summary>
    public static string Normalize(string? value)
    {
        var code = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (code.Length == 0)
        {
            return string.Empty;
        }

        // « fr_FR » et « fr-FR » comptent comme « fr ».
        var cut = code.IndexOfAny(['_', '-']);
        if (cut > 0)
        {
            code = code[..cut];
        }

        return Array.IndexOf(Supported, code) >= 0 ? code : string.Empty;
    }

    /// <summary>
    /// La langue a employer, du joueur vers la borne vers l'anglais. Le premier
    /// niveau reconnu gagne : un joueur japonais sur une borne francaise lit du
    /// japonais, un passant anonyme lit du francais.
    /// </summary>
    public static string Resolve(string? playerLocale, string? cabinetLocale)
    {
        var player = Normalize(playerLocale);
        if (player.Length > 0)
        {
            return player;
        }

        var cabinet = Normalize(cabinetLocale);
        return cabinet.Length > 0 ? cabinet : "en";
    }

    /// <summary>Une chaine d'annonce dans la langue resolue.</summary>
    public static string Get(string key, string locale)
    {
        var code = Normalize(locale);
        if (code.Length == 0)
        {
            code = "en";
        }

        if (Words.TryGetValue(code, out var table) && table.TryGetValue(key, out var text))
        {
            return text;
        }

        // Repli cle par cle : une chaine ajoutee au francais et pas encore
        // traduite s'affiche en anglais plutot que de disparaitre.
        return Words["en"].TryGetValue(key, out var fallback) ? fallback : key;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Words = new()
    {
        ["fr"] = new()
        {
            ["start_title"] = "Appuyez sur START pour commencer la partie",
            ["start_sub"] = "Elle sera mise en pause automatiquement - prête pour le départ",
            ["hold_title"] = "Ne touchez plus à rien !",
            ["hold_sub"] = "Partie en pause - départ imminent, attendez le décompte",
            ["reached_title"] = "🏁 Objectif atteint !",
            ["reached_sub"] = "Votre temps est enregistré - regardez le classement !",
            ["end_title"] = "🏁 Challenge terminé !",
            ["end_sub"] = "Classement sur l'écran de la salle - merci d'avoir joué !",
            ["countdown"] = "Départ dans…",
            ["go"] = "GO !",
            ["ready"] = "Tenez-vous prêt !",
            ["launching"] = "Le jeu se lance…",
            ["scan"] = "📱 Scannez pour participer - vos scores à votre nom",
            ["open_to_all"] = "Ouvert à tous",
        },
        ["en"] = new()
        {
            ["start_title"] = "Press START to begin",
            ["start_sub"] = "It will pause on its own - ready for the countdown",
            ["hold_title"] = "Hands off!",
            ["hold_sub"] = "Paused - the start is close, wait for the countdown",
            ["reached_title"] = "🏁 Target reached!",
            ["reached_sub"] = "Your time is recorded - watch the leaderboard!",
            ["end_title"] = "🏁 Challenge over!",
            ["end_sub"] = "Leaderboard on the venue screen - thanks for playing!",
            ["countdown"] = "Starting in…",
            ["go"] = "GO!",
            ["ready"] = "Get ready!",
            ["launching"] = "Launching the game…",
            ["scan"] = "📱 Scan to join - your scores under your name",
            ["open_to_all"] = "Open to all",
        },
        ["es"] = new()
        {
            ["start_title"] = "Pulsa START para empezar la partida",
            ["start_sub"] = "Se pausará sola - lista para la salida",
            ["hold_title"] = "¡No toques nada!",
            ["hold_sub"] = "En pausa - la salida es inminente, espera la cuenta atrás",
            ["reached_title"] = "🏁 ¡Objetivo alcanzado!",
            ["reached_sub"] = "Tu tiempo queda registrado - ¡mira la clasificación!",
            ["end_title"] = "🏁 ¡Reto terminado!",
            ["end_sub"] = "Clasificación en la pantalla de la sala - ¡gracias por jugar!",
            ["countdown"] = "Salida en…",
            ["go"] = "¡YA!",
            ["ready"] = "¡Prepárate!",
            ["launching"] = "Lanzando el juego…",
            ["scan"] = "📱 Escanea para participar - tus puntuaciones a tu nombre",
            ["open_to_all"] = "Abierto a todos",
        },
        ["ja"] = new()
        {
            ["start_title"] = "START を押してプレイ開始",
            ["start_sub"] = "自動で一時停止します - スタートの準備へ",
            ["hold_title"] = "そのままお待ちください",
            ["hold_sub"] = "一時停止中 - まもなくスタート、カウントをお待ちください",
            ["reached_title"] = "🏁 目標達成！",
            ["reached_sub"] = "タイムを記録しました - ランキングをご覧ください",
            ["end_title"] = "🏁 チャレンジ終了！",
            ["end_sub"] = "順位は会場の画面に - ご参加ありがとうございました",
            ["countdown"] = "スタートまで…",
            ["go"] = "GO！",
            ["ready"] = "ご準備ください",
            ["launching"] = "ゲームを起動しています…",
            ["scan"] = "📱 スキャンして参加 - スコアはあなたの名前で",
            ["open_to_all"] = "どなたでも参加できます",
        },
        ["zh"] = new()
        {
            ["start_title"] = "按 START 开始游戏",
            ["start_sub"] = "它会自动暂停 - 等待发车",
            ["hold_title"] = "请勿操作！",
            ["hold_sub"] = "已暂停 - 马上开始，请等待倒数",
            ["reached_title"] = "🏁 达成目标！",
            ["reached_sub"] = "你的成绩已记录 - 看看排行榜！",
            ["end_title"] = "🏁 挑战结束！",
            ["end_sub"] = "排名显示在场馆屏幕上 - 感谢参与！",
            ["countdown"] = "倒数…",
            ["go"] = "GO！",
            ["ready"] = "请准备！",
            ["launching"] = "正在启动游戏…",
            ["scan"] = "📱 扫码参与 - 成绩记在你的名下",
            ["open_to_all"] = "所有人皆可参加",
        },
        ["ko"] = new()
        {
            ["start_title"] = "START를 눌러 시작하세요",
            ["start_sub"] = "자동으로 일시정지됩니다 - 출발 준비",
            ["hold_title"] = "손을 떼 주세요!",
            ["hold_sub"] = "일시정지 - 곧 시작합니다, 카운트다운을 기다려 주세요",
            ["reached_title"] = "🏁 목표 달성!",
            ["reached_sub"] = "기록이 저장되었습니다 - 순위표를 확인하세요!",
            ["end_title"] = "🏁 챌린지 종료!",
            ["end_sub"] = "순위는 매장 화면에 - 참여해 주셔서 감사합니다!",
            ["countdown"] = "시작까지…",
            ["go"] = "GO!",
            ["ready"] = "준비하세요!",
            ["launching"] = "게임을 실행하는 중…",
            ["scan"] = "📱 스캔해서 참여 - 점수는 당신의 이름으로",
            ["open_to_all"] = "누구나 참여 가능",
        },
    };
}
