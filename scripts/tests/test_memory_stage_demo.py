"""The multi-turn demo must finish delivery before starting another turn."""
import os
import unittest
from unittest.mock import patch

from scripts.demo_memory_stage_b import demonstrate


class MemoryStageDemoTests(unittest.TestCase):
    def test_independent_turns_reinforce_and_permissions_preserve_scores(self):
        with patch.dict(os.environ, {"MEMORY_COLLECTION_ENABLED": "1", "MEMORY_REINFORCEMENT_ENABLED": "1"}):
            result = demonstrate()
        self.assertEqual(result["temporary_scores"], [0.3, 0.4, 0.5, 0.6, 0.7])
        self.assertEqual(result["stable_scores"], [0.6, 0.7])
        self.assertTrue(result["blocked_is_excluded"])
        self.assertEqual(result["restored_score"], 0.7)
        self.assertEqual(result["lore_publication"]["published"], 2)
        self.assertFalse(result["production_data_accessed"])
