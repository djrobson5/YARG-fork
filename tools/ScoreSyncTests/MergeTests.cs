using System;
using System.Linq;
using NUnit.Framework;
using YARG.Scores.Sync;
using static YARG.ScoreSyncTests.Fixtures;

namespace YARG.ScoreSyncTests
{
    public class MergeTests
    {
        private static readonly Guid Alice = new("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid Bob = new("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid AliceOnLaptop = new("aaaaaaaa-1111-1111-1111-000000000003");
        private static readonly Guid Carol = new("cccccccc-0000-0000-0000-000000000004");

        private static ScoreSyncData LocalWithAlice()
        {
            var local = new ScoreSyncData();
            local.Profiles.Add(Profile(Alice, "Alice"));
            local.Players.Add(Player(Alice, "Alice"));
            local.Games.Add(Game(SongA, T0, 1000, Score(Alice, 1000)));
            return local;
        }

        #region Games

        [Test]
        public void MatchingGameIsSkipped()
        {
            var local = LocalWithAlice();
            var source = Copy(local);

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.GamesToInsert, Is.Empty);
            Assert.That(plan.GamesSkipped, Is.EqualTo(1));
            Assert.That(plan.HasChanges, Is.False);
        }

        [Test]
        public void GameDifferingInAnyKeyFieldIsInserted()
        {
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.Games.Add(Game(SongB, T0, 1000, Score(Alice, 1000)));     // other song
            source.Games.Add(Game(SongA, T0 + 1, 1000, Score(Alice, 1000))); // one tick later
            source.Games.Add(Game(SongA, T0, 1001, Score(Alice, 1001)));     // other band score

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.GamesToInsert, Has.Count.EqualTo(3));
            Assert.That(plan.PlayerScoresToInsert, Is.EqualTo(3));
            Assert.That(plan.GamesSkipped, Is.Zero);
        }

        [Test]
        public void MatchingGameSkipsItsPlayerScoresToo()
        {
            // The local copy of the game has one player; the source copy has two. The game
            // matches, so none of the source's player scores for it are added
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.Profiles.Add(Profile(Bob, "Bob"));
            source.Games.Add(Game(SongA, T0, 1000, Score(Alice, 1000), Score(Bob, 500)));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.GamesToInsert, Is.Empty);
            Assert.That(plan.PlayerScoresToInsert, Is.Zero);
        }

        [Test]
        public void GameListedTwiceInSourceIsInsertedOnce()
        {
            var local = new ScoreSyncData();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.Games.Add(Game(SongA, T0, 1000, Score(Alice, 1000)));
            source.Games.Add(Game(SongA, T0, 1000, Score(Alice, 1000)));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.GamesToInsert, Has.Count.EqualTo(1));
            Assert.That(plan.GamesSkipped, Is.EqualTo(1));
        }

        [Test]
        public void InsertedGameKeepsItsFieldsAndReplayFileName()
        {
            var local = new ScoreSyncData();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            var game = Game(SongA, T0, 1234, Score(Alice, 1234));
            game.ReplayFileName = "Song-2026-09-01.replay";
            game.SongSpeed = 0.75f;
            game.HasBots = true;
            source.Games.Add(game);

            var inserted = ScoreSyncMerge.Plan(local, source).GamesToInsert.Single();

            Assert.That(inserted.ReplayFileName, Is.EqualTo("Song-2026-09-01.replay"));
            Assert.That(inserted.SongSpeed, Is.EqualTo(0.75f));
            Assert.That(inserted.HasBots, Is.True);
            Assert.That(inserted.DateTicks, Is.EqualTo(T0));
            Assert.That(inserted.SongChecksum, Is.EqualTo(SongA));
            Assert.That(inserted.PlayerScores.Single().Score, Is.EqualTo(1234));
        }

        [Test]
        public void PlanDoesNotMutateSource()
        {
            var local = new ScoreSyncData();
            local.Profiles.Add(Profile(Alice, "Alice"));
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(AliceOnLaptop, "Alice"));
            source.Games.Add(Game(SongA, T0, 1000, Score(AliceOnLaptop, 1000)));
            source.SectionCompletions.Add(Completion(AliceOnLaptop, SongA, 0, 4, T0));
            source.SectionProgress.Add(Progress(AliceOnLaptop, SongA, 4, 1, T0));

            ScoreSyncMerge.Plan(local, source);

            Assert.That(ScorePlayers(source), Is.All.EqualTo(AliceOnLaptop));
            Assert.That(source.SectionCompletions.Single().PlayerId, Is.EqualTo(AliceOnLaptop));
            Assert.That(source.SectionProgress.Single().PlayerId, Is.EqualTo(AliceOnLaptop));
        }

        #endregion

        #region Profiles

        [Test]
        public void ProfileWithLocalIdMatchesById()
        {
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            // Renamed on the other PC: the ID still wins
            source.Profiles.Add(Profile(Alice, "Alice (laptop)"));
            source.Players.Add(Player(Alice, "Alice (laptop)"));

            var plan = ScoreSyncMerge.Plan(local, source);

            var resolution = plan.Profiles.Single();
            Assert.That(resolution.Match, Is.EqualTo(ProfileMatch.ById));
            Assert.That(resolution.LocalId, Is.EqualTo(Alice));
            Assert.That(plan.ProfilesToCreate, Is.Empty);
            Assert.That(plan.PlayersToInsert, Is.Empty, "local name wins");
        }

        [Test]
        public void ProfileWithSameNameIsRemapped()
        {
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(AliceOnLaptop, "  aLICE "));
            source.Players.Add(Player(AliceOnLaptop, "  aLICE "));
            source.Games.Add(Game(SongB, T0, 900, Score(AliceOnLaptop, 900)));
            source.SectionCompletions.Add(Completion(AliceOnLaptop, SongB, 0, 3, T0));
            source.SectionProgress.Add(Progress(AliceOnLaptop, SongB, 3, 1, T0));

            var plan = ScoreSyncMerge.Plan(local, source);

            var resolution = plan.Profiles.Single();
            Assert.That(resolution.Match, Is.EqualTo(ProfileMatch.ByName));
            Assert.That(resolution.LocalId, Is.EqualTo(Alice));
            Assert.That(plan.PlayerIdMap[AliceOnLaptop], Is.EqualTo(Alice));
            Assert.That(plan.ProfilesToCreate, Is.Empty);
            Assert.That(plan.PlayersToInsert, Is.Empty, "Alice already has a Players row");

            Assert.That(plan.GamesToInsert.Single().PlayerScores.Single().PlayerId, Is.EqualTo(Alice));
            Assert.That(plan.SectionCompletionsToInsert.Single().PlayerId, Is.EqualTo(Alice));
            Assert.That(plan.SectionProgressWrites.Single().Row.PlayerId, Is.EqualTo(Alice));
        }

        [Test]
        public void UnknownProfileIsCreatedWithSourceId()
        {
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Carol, "Carol"));
            source.Players.Add(Player(Carol, "Carol"));
            source.Games.Add(Game(SongA, T0 + 5, 700, Score(Carol, 700)));

            var plan = ScoreSyncMerge.Plan(local, source);

            var resolution = plan.Profiles.Single();
            Assert.That(resolution.Match, Is.EqualTo(ProfileMatch.Created));
            Assert.That(resolution.LocalId, Is.EqualTo(Carol));
            Assert.That(plan.ProfilesToCreate.Single().Name, Is.EqualTo("Carol"));
            Assert.That(plan.PlayersToInsert.Single().Id, Is.EqualTo(Carol));
            Assert.That(plan.GamesToInsert.Single().PlayerScores.Single().PlayerId, Is.EqualTo(Carol));

            // The next sync matches the created profile by ID
            Apply(local, plan);
            var next = ScoreSyncMerge.Plan(local, source);
            Assert.That(next.Profiles.Single().Match, Is.EqualTo(ProfileMatch.ById));
            Assert.That(next.HasChanges, Is.False);
        }

        [Test]
        public void IdMatchBeatsNameMatch()
        {
            // The source profile has Bob's ID but Alice's name: it is Bob, renamed
            var local = LocalWithAlice();
            local.Profiles.Add(Profile(Bob, "Bob"));
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Bob, "Alice"));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.Profiles.Single().Match, Is.EqualTo(ProfileMatch.ById));
            Assert.That(plan.PlayerIdMap[Bob], Is.EqualTo(Bob));
        }

        [Test]
        public void TwoSourceProfilesWithOneNewNameCreateOneProfile()
        {
            var carol2 = Guid.NewGuid();
            var local = new ScoreSyncData();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Carol, "Carol"));
            source.Profiles.Add(Profile(carol2, "carol"));
            source.Games.Add(Game(SongA, T0, 1, Score(carol2, 1)));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.ProfilesToCreate, Has.Count.EqualTo(1));
            Assert.That(plan.PlayerIdMap[carol2], Is.EqualTo(Carol));
            Assert.That(plan.GamesToInsert.Single().PlayerScores.Single().PlayerId, Is.EqualTo(Carol));
        }

        [Test]
        public void PlayerWithoutProfileKeepsIdAndCreatesNoProfile()
        {
            // A profile deleted on the source PC: its Players row and scores remain
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Players.Add(Player(Carol, "Carol"));
            source.Games.Add(Game(SongB, T0, 50, Score(Carol, 50)));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.Profiles, Is.Empty);
            Assert.That(plan.PlayerIdMap[Carol], Is.EqualTo(Carol));
            Assert.That(plan.PlayersToInsert.Single().Id, Is.EqualTo(Carol));
            Assert.That(plan.GamesToInsert.Single().PlayerScores.Single().PlayerId, Is.EqualTo(Carol));
        }

        [Test]
        public void PlayerWithoutProfileMatchesLocalProfileByName()
        {
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Players.Add(Player(AliceOnLaptop, "Alice"));
            source.Games.Add(Game(SongB, T0, 50, Score(AliceOnLaptop, 50)));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.PlayerIdMap[AliceOnLaptop], Is.EqualTo(Alice));
            Assert.That(plan.PlayersToInsert, Is.Empty);
            Assert.That(plan.GamesToInsert.Single().PlayerScores.Single().PlayerId, Is.EqualTo(Alice));
        }

        [Test]
        public void ScoreWithUnlistedPlayerKeepsItsId()
        {
            // No profile and no Players row: nothing to match on, so the ID passes through
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Games.Add(Game(SongB, T0, 50, Score(Carol, 50)));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.PlayerIdMap[Carol], Is.EqualTo(Carol));
            Assert.That(plan.PlayersToInsert, Is.Empty);
            Assert.That(plan.GamesToInsert.Single().PlayerScores.Single().PlayerId, Is.EqualTo(Carol));
        }

        #endregion

        #region Sections

        [Test]
        public void MissingCompletionsAreInsertedAndPresentOnesKeepEarlierDate()
        {
            var local = LocalWithAlice();
            local.SectionCompletions.Add(Completion(Alice, SongA, 0, 4, T0 + 100));
            local.SectionCompletions.Add(Completion(Alice, SongA, 1, 4, T0 + 100));
            local.SectionProgress.Add(Progress(Alice, SongA, 4, 2, T0 + 100));

            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.SectionCompletions.Add(Completion(Alice, SongA, 0, 4, T0 + 50));  // earlier
            source.SectionCompletions.Add(Completion(Alice, SongA, 1, 4, T0 + 200)); // later
            source.SectionCompletions.Add(Completion(Alice, SongA, 2, 4, T0 + 200)); // new
            source.SectionProgress.Add(Progress(Alice, SongA, 4, 3, T0 + 200));

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.SectionCompletionsToInsert.Single().SectionIndex, Is.EqualTo(2));
            var update = plan.SectionCompletionDateUpdates.Single();
            Assert.That(update.Local.SectionIndex, Is.EqualTo(0));
            Assert.That(update.FirstCompletedTicks, Is.EqualTo(T0 + 50));

            Apply(local, plan);
            var byIndex = local.SectionCompletions.ToDictionary(c => c.SectionIndex, c => c.FirstCompletedTicks);
            Assert.That(byIndex[0], Is.EqualTo(T0 + 50));
            Assert.That(byIndex[1], Is.EqualTo(T0 + 100));
            Assert.That(byIndex[2], Is.EqualTo(T0 + 200));
        }

        [Test]
        public void ProgressIsRecomputedFromTheUnion()
        {
            // Each PC perfected different sections; neither progress row's count is right for
            // the union. The source row is newer, so its SectionCount and LastUpdated win
            var local = LocalWithAlice();
            local.SectionCompletions.Add(Completion(Alice, SongA, 0, 5, T0));
            local.SectionCompletions.Add(Completion(Alice, SongA, 1, 5, T0));
            local.SectionProgress.Add(Progress(Alice, SongA, 5, 2, T0));

            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.SectionCompletions.Add(Completion(Alice, SongA, 1, 5, T0 + 10));
            source.SectionCompletions.Add(Completion(Alice, SongA, 3, 5, T0 + 10));
            source.SectionCompletions.Add(Completion(Alice, SongA, 4, 5, T0 + 10));
            source.SectionProgress.Add(Progress(Alice, SongA, 5, 3, T0 + 10));

            var plan = ScoreSyncMerge.Plan(local, source);

            var write = plan.SectionProgressWrites.Single();
            Assert.That(write.IsInsert, Is.False);
            Assert.That(write.Row.CompletedCount, Is.EqualTo(4), "sections 0, 1, 3, 4");
            Assert.That(write.Row.SectionCount, Is.EqualTo(5));
            Assert.That(write.Row.LastUpdatedTicks, Is.EqualTo(T0 + 10));
        }

        [Test]
        public void ProgressTakesSectionCountFromTheNewerRowAndClamps()
        {
            // The chart was re-scanned locally with fewer sections, more recently than the
            // source's run. The union has 3 completions but only 2 sections now count
            var local = LocalWithAlice();
            local.SectionCompletions.Add(Completion(Alice, SongA, 0, 2, T0 + 100));
            local.SectionProgress.Add(Progress(Alice, SongA, 2, 1, T0 + 100));

            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.SectionCompletions.Add(Completion(Alice, SongA, 1, 6, T0));
            source.SectionCompletions.Add(Completion(Alice, SongA, 5, 6, T0));
            source.SectionProgress.Add(Progress(Alice, SongA, 6, 2, T0));

            var write = ScoreSyncMerge.Plan(local, source).SectionProgressWrites.Single();

            Assert.That(write.Row.SectionCount, Is.EqualTo(2));
            Assert.That(write.Row.CompletedCount, Is.EqualTo(2));
            Assert.That(write.Row.LastUpdatedTicks, Is.EqualTo(T0 + 100));
        }

        [Test]
        public void ProgressMissingLocallyIsInserted()
        {
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.SectionCompletions.Add(Completion(Alice, SongB, 2, 8, T0));
            source.SectionProgress.Add(Progress(Alice, SongB, 8, 1, T0));

            var write = ScoreSyncMerge.Plan(local, source).SectionProgressWrites.Single();

            Assert.That(write.IsInsert, Is.True);
            Assert.That(write.Row.CompletedCount, Is.EqualTo(1));
            Assert.That(write.Row.SectionCount, Is.EqualTo(8));
        }

        [Test]
        public void ZeroOfNProgressIsSynced()
        {
            // A valid run that perfected nothing still leaves a denominator to show (0/12)
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.SectionProgress.Add(Progress(Alice, SongB, 12, 0, T0));

            var write = ScoreSyncMerge.Plan(local, source).SectionProgressWrites.Single();

            Assert.That(write.IsInsert, Is.True);
            Assert.That(write.Row.CompletedCount, Is.Zero);
            Assert.That(write.Row.SectionCount, Is.EqualTo(12));
        }

        [Test]
        public void LocalProgressIsRecountedWhenCompletionsArriveWithoutASourceRow()
        {
            var local = LocalWithAlice();
            local.SectionCompletions.Add(Completion(Alice, SongA, 0, 4, T0));
            local.SectionProgress.Add(Progress(Alice, SongA, 4, 1, T0));

            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            source.SectionCompletions.Add(Completion(Alice, SongA, 3, 4, T0));

            var write = ScoreSyncMerge.Plan(local, source).SectionProgressWrites.Single();

            Assert.That(write.IsInsert, Is.False);
            Assert.That(write.Row.CompletedCount, Is.EqualTo(2));
            Assert.That(write.Row.LastUpdatedTicks, Is.EqualTo(T0));
        }

        [Test]
        public void HarmonyPartsStaySeparate()
        {
            var local = LocalWithAlice();
            var harm1 = Completion(Alice, SongA, 0, 4, T0);
            harm1.HarmonyIndex = 1;
            local.SectionCompletions.Add(harm1);

            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(Alice, "Alice"));
            var harm2 = Completion(Alice, SongA, 0, 4, T0);
            harm2.HarmonyIndex = 2;
            source.SectionCompletions.Add(harm2);

            var plan = ScoreSyncMerge.Plan(local, source);

            Assert.That(plan.SectionCompletionsToInsert.Single().HarmonyIndex, Is.EqualTo(2));
        }

        [Test]
        public void RemappedDuplicatesMergeIntoOneRow()
        {
            // Two source profiles both resolve to local Alice by name, and both perfected the
            // same section. One completion row with the earlier date, one progress row
            var aliceTwo = Guid.NewGuid();
            var local = LocalWithAlice();
            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(AliceOnLaptop, "Alice"));
            source.Profiles.Add(Profile(aliceTwo, "ALICE"));
            source.SectionCompletions.Add(Completion(AliceOnLaptop, SongB, 0, 3, T0 + 9));
            source.SectionCompletions.Add(Completion(aliceTwo, SongB, 0, 3, T0 + 4));
            source.SectionProgress.Add(Progress(AliceOnLaptop, SongB, 3, 1, T0 + 9));
            source.SectionProgress.Add(Progress(aliceTwo, SongB, 3, 1, T0 + 4));

            var plan = ScoreSyncMerge.Plan(local, source);

            var completion = plan.SectionCompletionsToInsert.Single();
            Assert.That(completion.PlayerId, Is.EqualTo(Alice));
            Assert.That(completion.FirstCompletedTicks, Is.EqualTo(T0 + 4));

            var write = plan.SectionProgressWrites.Single();
            Assert.That(write.Row.LastUpdatedTicks, Is.EqualTo(T0 + 9));
            Assert.That(write.Row.CompletedCount, Is.EqualTo(1));
        }

        #endregion

        #region Idempotence and transitivity

        private static (ScoreSyncData local, ScoreSyncData source) Divergent()
        {
            var local = LocalWithAlice();
            local.Profiles.Add(Profile(Bob, "Bob"));
            local.Players.Add(Player(Bob, "Bob"));
            local.Games.Add(Game(SongB, T0 + 1, 300, Score(Alice, 200), Score(Bob, 100)));
            local.SectionCompletions.Add(Completion(Alice, SongA, 0, 4, T0 + 20));
            local.SectionProgress.Add(Progress(Alice, SongA, 4, 1, T0 + 20));

            var source = new ScoreSyncData();
            source.Profiles.Add(Profile(AliceOnLaptop, "alice"));
            source.Profiles.Add(Profile(Carol, "Carol"));
            source.Players.Add(Player(AliceOnLaptop, "alice"));
            source.Players.Add(Player(Carol, "Carol"));
            source.Games.Add(Game(SongA, T0, 1000, Score(AliceOnLaptop, 1000)));    // same as local
            source.Games.Add(Game(SongA, T0 + 7, 1500, Score(AliceOnLaptop, 1500)));
            source.Games.Add(Game(SongB, T0 + 8, 800, Score(Carol, 400), Score(AliceOnLaptop, 400)));
            source.SectionCompletions.Add(Completion(AliceOnLaptop, SongA, 0, 4, T0 + 5));
            source.SectionCompletions.Add(Completion(AliceOnLaptop, SongA, 2, 4, T0 + 30));
            source.SectionCompletions.Add(Completion(Carol, SongB, 1, 6, T0 + 8));
            source.SectionProgress.Add(Progress(AliceOnLaptop, SongA, 4, 2, T0 + 30));
            source.SectionProgress.Add(Progress(Carol, SongB, 6, 1, T0 + 8));
            source.SectionProgress.Add(Progress(Carol, SongA, 4, 0, T0 + 8));

            return (local, source);
        }

        [Test]
        public void SecondImportOfTheSameFileChangesNothing()
        {
            var (local, source) = Divergent();

            var first = ScoreSyncMerge.Plan(local, source);
            Assert.That(first.HasChanges, Is.True);
            Apply(local, first);

            var second = ScoreSyncMerge.Plan(local, source);

            Assert.That(second.HasChanges, Is.False);
            Assert.That(second.GamesSkipped, Is.EqualTo(source.Games.Count));
        }

        [Test]
        public void DivergentMergeProducesTheUnion()
        {
            var (local, source) = Divergent();

            Apply(local, ScoreSyncMerge.Plan(local, source));

            Assert.That(local.Games, Has.Count.EqualTo(4));
            Assert.That(local.Profiles.Select(p => p.Id), Is.EquivalentTo(new[] { Alice, Bob, Carol }));
            Assert.That(ScorePlayers(local), Does.Not.Contain(AliceOnLaptop));

            var aliceSongA = local.SectionProgress.Single(p => p.PlayerId == Alice && p.SongChecksum.SequenceEqual(SongA));
            Assert.That(aliceSongA.CompletedCount, Is.EqualTo(2), "sections 0 and 2");
            Assert.That(aliceSongA.LastUpdatedTicks, Is.EqualTo(T0 + 30));
            Assert.That(local.SectionCompletions.Single(c => c.PlayerId == Alice && c.SectionIndex == 0)
                .FirstCompletedTicks, Is.EqualTo(T0 + 5));
            Assert.That(local.SectionProgress, Has.Count.EqualTo(3));
        }

        [Test]
        public void MergeIsTransitiveThroughAThirdPc()
        {
            // PC A merges PC B's file, then exports everything. PC C, which has never seen B,
            // gets B's games through A's file, and syncing B's own file afterwards adds nothing
            var (pcA, pcB) = Divergent();
            Apply(pcA, ScoreSyncMerge.Plan(pcA, pcB));

            var pcC = new ScoreSyncData();
            pcC.Profiles.Add(Profile(Alice, "Alice"));

            var fromA = ScoreSyncMerge.Plan(pcC, Copy(pcA));
            Apply(pcC, fromA);
            Assert.That(pcC.Games, Has.Count.EqualTo(pcA.Games.Count));

            var fromB = ScoreSyncMerge.Plan(pcC, pcB);
            Assert.That(fromB.HasChanges, Is.False);
        }

        [Test]
        public void MergingBothWaysConverges()
        {
            var (a, b) = Divergent();
            var aBefore = Copy(a);

            Apply(a, ScoreSyncMerge.Plan(a, b));
            Apply(b, ScoreSyncMerge.Plan(b, aBefore));

            Assert.That(a.Games.Select(ScoreSyncMerge.GameKey), Is.EquivalentTo(b.Games.Select(ScoreSyncMerge.GameKey)));
            Assert.That(a.SectionCompletions, Has.Count.EqualTo(b.SectionCompletions.Count));
            Assert.That(a.SectionProgress.Sum(p => p.CompletedCount), Is.EqualTo(b.SectionProgress.Sum(p => p.CompletedCount)));
        }

        #endregion
    }
}
