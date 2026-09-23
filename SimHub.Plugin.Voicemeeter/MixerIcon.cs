using System.Windows;
using System.Windows.Media;

namespace SimHub.Plugin.Voicemeeter
{
    /// <summary>
    /// Draws the plugin's left-menu icon in code (a simple 3-channel mixer/fader glyph) instead of
    /// shipping a bitmap resource. SimHub appears to render this icon as a mask (alpha only, ignoring
    /// actual RGB), so every filled/stroked shape shows up in the same colour regardless of what's
    /// specified here - there's no point drawing an outline in a different colour than the fill, and
    /// the brush colour chosen below is basically arbitrary as far as the rendered result goes.
    /// </summary>
    internal static class MixerIcon
    {
        public static readonly ImageSource Value = Build();

        private static ImageSource Build()
        {
            var group = new DrawingGroup();
            using (DrawingContext dc = group.Open())
            {
                var trackPen = new Pen(Brushes.White, 1.5)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };

                double[] trackX = { 5, 12, 19 };
                double[] capY = { 15, 7, 11 };
                const double trackTop = 2;
                const double trackBottom = 22;
                const double capWidth = 5;
                const double capHeight = 3;

                foreach (double x in trackX)
                {
                    dc.DrawLine(trackPen, new Point(x, trackTop), new Point(x, trackBottom));
                }

                for (int i = 0; i < trackX.Length; i++)
                {
                    var capRect = new Rect(trackX[i] - (capWidth / 2), capY[i] - (capHeight / 2), capWidth, capHeight);
                    dc.DrawRoundedRectangle(Brushes.White, null, capRect, 1, 1);
                }
            }

            group.Freeze();
            return new DrawingImage(group);
        }
    }
}
