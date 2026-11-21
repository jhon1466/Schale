using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Schale.FlatData;
using Schale.MX.GameLogic.Parcel;

namespace Schale.Data.GameModel
{
    public class StudentFrameDBServer : ParcelBase
    {
        [JsonIgnore]
        public virtual AccountDBServer? Account { get; set; }

        public long AccountServerId { get; set; }

        [Key]
        public long ServerId { get; set; }

        public override ParcelType Type => ParcelType.StudentFrame;

        public long UniqueId { get; set; }
        public DateTime ReceiveDate { get; set; }

        [NotMapped]
        [JsonIgnore]
        public override IEnumerable<ParcelInfo>? ParcelInfos { get; }
    }

    public static class StudentFrameDBServerExtensions
    {
        public static IQueryable<StudentFrameDBServer> GetAccountStudentFrames(this SchaleDataContext context, long accountId)
        {
            return context.StudentFrames.Where(x => x.AccountServerId == accountId);
        }

        public static List<StudentFrameDBServer> AddStudentFrames(this SchaleDataContext context, long accountId, params StudentFrameDBServer[] frames)
        {
            if (frames == null || frames.Length == 0)
                return [];

            foreach (var frame in frames)
            {
                frame.AccountServerId = accountId;
                context.StudentFrames.Add(frame);
            }

            return [.. frames];
        }
    }
}
