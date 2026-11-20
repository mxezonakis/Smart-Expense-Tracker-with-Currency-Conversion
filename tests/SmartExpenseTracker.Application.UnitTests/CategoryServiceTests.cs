using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using SmartExpenseTracker.Application.Categories;

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
            var store = A.Fake<ICategoryStore>();
            var service = new CategoryService(store);
            var token = TestContext.CurrentContext.CancellationToken;
            var name = "a B".PadRight(100, 'x');

            await service.CreateAsync("\u2003" + name + "\t", token);
            await service.RenameAsync(1, " " + name + " ", token);

            A.CallTo(() => store.CreateAsync(name, token)).MustHaveHappenedOnceExactly();
            A.CallTo(() => store.RenameAsync(1, name, token)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task MissingCategoryRemainsDistinguishableFromDuplicate()
        {
            var store = A.Fake<ICategoryStore>();
            var service = new CategoryService(store);
            var token = TestContext.CurrentContext.CancellationToken;
            A.CallTo(() => store.FindAsync(7, token)).Returns((Category?)null);
            A.CallTo(() => store.CreateAsync("Food", token)).ThrowsAsync(new DuplicateCategoryNameException(new Exception()));

            (await service.FindAsync(7, token)).Should().BeNull();
            (await service.RenameAsync(7, "Food", token)).Should().BeFalse();
            (await service.DeleteAsync(7, token)).Should().BeFalse();
            Assert.ThrowsAsync<DuplicateCategoryNameException>(() => service.CreateAsync(" Food ", token));
        }
    }
}
