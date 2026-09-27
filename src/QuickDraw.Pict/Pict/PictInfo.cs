using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    /// <summary>Header and metadata of a picture.</summary>
    public sealed class PictInfo
    {
        internal PictInfo(int version, bool extendedVersion2, PictRect pictureFrame, PictRect bounds,
            double horizontalResolution, double verticalResolution)
        {
            Version = version;
            IsExtendedVersion2 = extendedVersion2;
            PictureFrame = pictureFrame;
            Bounds = bounds;
            HorizontalResolution = horizontalResolution;
            VerticalResolution = verticalResolution;
        }

        internal readonly List<PictComment> CommentList = new List<PictComment>();

        /// <summary>1 or 2 (version 2 includes extended version 2).</summary>
        public int Version { get; }

        /// <summary>True for an extended version 2 picture (created by <c>OpenCPicture</c>, header version −2).</summary>
        public bool IsExtendedVersion2 { get; }

        /// <summary>The picture's <c>picFrame</c>: its bounding rectangle at 72 dpi.</summary>
        public PictRect PictureFrame { get; }

        /// <summary>
        /// The rectangle the picture's opcodes draw in, which the decoded canvas covers: the header's optimal source
        /// rectangle for an extended version 2 picture, otherwise <see cref="PictureFrame"/>.
        /// </summary>
        public PictRect Bounds { get; }

        /// <summary>Horizontal resolution of <see cref="Bounds"/> in dpi (72 unless an extended header says otherwise).</summary>
        public double HorizontalResolution { get; }

        /// <summary>Vertical resolution of <see cref="Bounds"/> in dpi.</summary>
        public double VerticalResolution { get; }

        /// <summary>The embedded ICC profile (picture comment 224, reassembled across begin/continuation/end), if any.</summary>
        public byte[]? IccProfile { get; internal set; }

        /// <summary>All picture comments, in order.</summary>
        public IReadOnlyList<PictComment> Comments => CommentList;

        // ICC profile accumulation (comment kind 224: u32 selector 0 begin / 1 continuation / 2 end, then data).
        private List<byte>? iccAccumulator;

        internal void AddComment(int kind, byte[] data)
        {
            CommentList.Add(new PictComment(kind, data));
            if (kind != 224 || data.Length < 4) return;
            uint selector = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
            var payload = data.AsSpan(4);
            switch (selector)
            {
                case 0:
                    iccAccumulator = new List<byte>(payload.ToArray());
                    break;
                case 1:
                    iccAccumulator?.AddRange(payload.ToArray());
                    break;
                case 2:
                    if (iccAccumulator != null) IccProfile = iccAccumulator.ToArray();
                    iccAccumulator = null;
                    break;
            }
        }
    }
}
