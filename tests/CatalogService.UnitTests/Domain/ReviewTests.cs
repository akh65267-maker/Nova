using CatalogService.Domain;

namespace CatalogService.UnitTests.Domain;

public class ReviewTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Rating_MustBeFromOneToFive(int rating)
    {
        Assert.Throws<ArgumentException>(() => new Review(Guid.NewGuid(), Guid.NewGuid(), "a@b.c", rating, "x"));
    }

    [Fact]
    public void Body_IsTrimmedAndOptional()
    {
        Assert.Equal("Good", new Review(Guid.NewGuid(), Guid.NewGuid(), "a@b.c", 5, "  Good ").Body);
        Assert.Equal(string.Empty, new Review(Guid.NewGuid(), Guid.NewGuid(), "a@b.c", 5, null).Body);
    }

    [Fact]
    public void Body_IsLimited()
    {
        Assert.Throws<ArgumentException>(() => new Review(Guid.NewGuid(), Guid.NewGuid(), "a@b.c", 5, new string('x', Review.MaxBodyLength + 1)));
    }

    [Theory]
    [InlineData("jane@example.com", "J***")]
    [InlineData("  bob@example.com", "B***")]
    [InlineData("", "Customer")]
    public void Author_ShowsOnlyTheFirstLetterOfTheEmail(string email, string expected)
    {
        Assert.Equal(expected, Review.AuthorNameFor(email));
    }

    [Fact]
    public void Edit_ChangesRatingAndBody()
    {
        var review = new Review(Guid.NewGuid(), Guid.NewGuid(), "a@b.c", 5, "Great");

        review.Edit(2, "Worse");

        Assert.Equal(2, review.Rating);
        Assert.Equal("Worse", review.Body);
    }

    [Fact]
    public void Product_AverageIsRoundedToOneDecimal_AndNullWithoutReviews()
    {
        var product = new Product("Widget", "d", 1m, "Electronics");
        Assert.Null(product.RatingAverage);

        product.SetRating(3, 13);

        Assert.Equal(4.3, product.RatingAverage);
    }
}
