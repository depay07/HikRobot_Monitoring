using System;
using System.Threading.Tasks;

namespace SC6000DualMonitor
{
    internal static class SettingsSaveCompletion
    {
        // The transaction includes verification, persistence and inspection resume.
        // Never run a success refresh after a failed transaction or roll back saved values
        // because a display-only operation failed.
        public static async Task<string> ApplyAsync(Action apply, Func<Task> refreshGraphics)
        {
            apply();
            if (refreshGraphics != null)
                try { await refreshGraphics(); }
                catch (Exception ex)
                {
                    return "설정 저장과 검사 재개는 완료했으나 화면 연결 갱신에 실패했습니다.\r\n" +
                        ex.GetBaseException().Message;
                }
            return null;
        }
    }
}
