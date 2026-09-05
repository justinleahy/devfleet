using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PiCommandCenter.Infrastructure.Completion;

public sealed class RequestResultRowConfiguration : IEntityTypeConfiguration<RequestResultRow>
{
    public void Configure(EntityTypeBuilder<RequestResultRow> builder)
    {
        builder.ToTable("RequestResults");

        builder.HasKey(r => r.RequestId);
        builder.Property(r => r.RequestId).HasColumnType("TEXT");
        builder.Property(r => r.SummaryMarkdown).IsRequired();
        builder.Property(r => r.ChangedFilesJson).IsRequired();
        builder.Property(r => r.ReviewFindingsJson).IsRequired();
        builder.Property(r => r.VerificationSummaryJson).IsRequired();
        builder.Property(r => r.CreatedAtUtcTicks).HasColumnType("INTEGER");
    }
}
