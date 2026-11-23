using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using SmartExpenseTracker.Application.Categories;
using SmartExpenseTracker.Persistence.Categories;

namespace SmartExpenseTracker.Application.UnitTests
{
    public sealed class CategoryServiceTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void InvalidNamesFailBeforeWriting(string? name)
        {
            AssertInvalid(name);
        }

        [Test]
        public void OverlongNameFailsBeforeWriting()
        {
            AssertInvalid(new string('x', 101));
        }

        private static void AssertInvalid(string? name)
        {
            var store = A.Fake<ICategoryStore>();
            var service = new CategoryService(store);

            Assert.ThrowsAsync<InvalidCategoryNameException>(() => service.CreateAsync(name, CancellationToken.None));
            Assert.ThrowsAsync<InvalidCategoryNameException>(() => service.RenameAsync(1, name, CancellationToken.None));
            Fake.GetCalls(store).Should().BeEmpty();
        }

        [Test]
        public async Task WritesTrimOnlyOuterWhitespaceAndForwardCancellation()
        {
            // Arrange
            var store = A.Fake<ICategoryStore>();
            var service = new CategoryService(store);
            var token = TestContext.CurrentContext.CancellationToken;
            var name = "a B".PadRight(100, 'x');

            A.CallTo(() => store.CreateAsync(name, token)).Returns(new Category(7, name));
            A.CallTo(() => store.RenameAsync(1, name, token)).Returns(true);

            // Act
            var actual = (await service.CreateAsync("\u2003" + name + "\t", token));

            // Assert
            actual.Should().Be(new Category(7, name));

            // Act
            var actual2 = (await service.RenameAsync(1, " " + name + " ", token));

            // Assert
            actual2.Should().BeTrue();

            A.CallTo(() => store.CreateAsync(name, token)).MustHaveHappenedOnceExactly();
            A.CallTo(() => store.RenameAsync(1, name, token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task MissingCategoryRemainsDistinguishableFromDuplicate()
        {
            // Arrange
            var store = A.Fake<ICategoryStore>();
            var service = new CategoryService(store);
            var token = TestContext.CurrentContext.CancellationToken;
            A.CallTo(() => store.FindAsync(7, token)).Returns((Category?)null);
            A.CallTo(() => store.CreateAsync("Food", token)).ThrowsAsync(new DuplicateCategoryNameException(new Exception()));

            // Act
            var actual = (await service.FindAsync(7, token));

            // Assert
            actual.Should().BeNull();

            // Act
            var actual2 = (await service.RenameAsync(7, "Food", token));

            // Assert
            actual2.Should().BeFalse();

            // Act
            var actual3 = (await service.DeleteAsync(7, token));

            // Assert
            actual3.Should().BeFalse();

            // Act
            AsyncTestDelegate act = () => service.CreateAsync(" Food ", token);

            // Assert
            Assert.ThrowsAsync<DuplicateCategoryNameException>(act);
        }
    }
}
