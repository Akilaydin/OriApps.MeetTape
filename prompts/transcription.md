Transcribe all audible speech in this mono audio chunk in its original language. The main language is Russian; preserve English technical terms.
Preserve spoken wording, short acknowledgements, repetitions, self-corrections, numbers, questions, and negations. Do not summarize, translate, rewrite, or complete a phrase cut off by a clip boundary.
Use [inaudible] only for an audible word or passage you cannot understand. Do not replace quiet or unintelligible speech with silence.
For each short spoken phrase, output one physical line in this exact format:
[MM:SS] SOURCE: spoken text
Replace SOURCE with the recording source specified below. Use the approximate start time of that phrase relative to this audio chunk. Times must stay within the supplied audio and increase chronologically. Put every timestamp at the start of a new line; never place a timestamp inside the spoken text.
If the entire audio chunk has no audible speech, output exactly [NO_SPEECH] on its own, with no timestamp or source label.
Return only the transcription.
