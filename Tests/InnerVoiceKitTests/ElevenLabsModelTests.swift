import Foundation
import Testing
@testable import InnerVoiceKit

@Test func elevenLabsModelsComeFromTheAccountNewestFirst() throws {
    let json = #"""
    [{"model_id":"eleven_multilingual_v2","name":"Eleven Multilingual v2","can_do_text_to_speech":true,"maximum_text_length_per_request":10000,"model_rates":{"character_cost_multiplier":1.0}},
     {"model_id":"eleven_flash_v2_5","name":"Eleven Flash v2.5","can_do_text_to_speech":true,"model_rates":{"character_cost_multiplier":0.5}},
     {"model_id":"eleven_english_sts_v2","name":"Speech to Speech","can_do_text_to_speech":false},
     {"model_id":"eleven_v4","name":"Eleven v4","can_do_text_to_speech":true,"model_rates":{"character_cost_multiplier":1.0}},
     {"model_id":"eleven_v5","name":"Eleven v5","can_do_text_to_speech":true,"model_rates":{"character_cost_multiplier":1.5}}]
    """#
    let models = try #require(ElevenLabsModel.parse(Data(json.utf8)))
    #expect(models.map(\.id) == ["eleven_v5", "eleven_v4", "eleven_multilingual_v2", "eleven_flash_v2_5"])
    let flash = try #require(models.first { $0.id == "eleven_flash_v2_5" })
    #expect(flash.credits(characters: 1001) == 501 && flash.rateText == "0.5 credits per character")
    #expect(!flash.supportsAudioTags)
    let v5 = models[0]
    #expect(v5.supportsAudioTags && v5.credits(characters: 1000) == 1500)  // a newer model needs no update
    #expect(models[1].rateText == "1 credit per character")
    #expect(ElevenLabsModel.parse(Data("{}".utf8)) == nil)
}
