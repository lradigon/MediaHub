# Veille Et Telegram

La page **Veille** surveille les films manquants et toutes les series/animes, reglee par defaut a 60 minutes et 3 recherches concurrentes. Les films deja en stock (chemin local ou association cloud) sont exclus des recherches et notifications ; leurs propositions en attente expirent, sans annuler les jobs deja lances. Le compteur ne comprend que les medias suivis. Les nouveaux medias eligibles sont automatiquement ajoutes a la veille.

Le premier scan reussi de chaque media etablit une reference silencieuse. Les scans suivants proposent uniquement les releases nouvelles compatibles avec le profil du media, ou le profil par defaut. Une nouvelle release initialement filtree peut devenir eligible au scan suivant ; celles de la reference initiale restent silencieuses. Les erreurs ne valident jamais une reference.

La veille utilise l'endpoint C411 par identifiant TMDB quand il est connu, sinon une recherche Torznab par titre. Les requetes partagent un espacement minimum de 5 secondes dans le processus. Le serveur est configure pour une recherche a la fois. Pour 281 medias, un tour demande au minimum environ 23 minutes 20 secondes, plus la latence et les pauses entre lots. Jackett et Prowlarr indiquent une limite Torznab de 15 requetes par minute ; le quota de l'endpoint TMDB reste a confirmer. L'intervalle repart de la fin du dernier scan reussi de chaque media ; les tours ne se superposent pas. Un 429 ou un refus d'acces suspend les recherches pour une heure. Les autres erreurs sont retentees apres 15 minutes.

## Scan Manuel Et Suivi

**Annuler le scan** arrete un passage manuel, meme en file ou en pause. Les recherches reseau en cours recoivent une annulation au prochain controle du worker (environ une seconde). Les resultats deja sauvegardes et les jobs medias ne sont pas supprimes. Les medias non termines apparaissent comme annules. L'annulation ne desactive pas la veille automatique et ne contourne pas une suspension C411 ; une nouvelle demande conserve son echeance de reprise.

Le mode **Parallelisme automatique** (valeur 0 en API) dimensionne la limite a quatre recherches par processeur disponible dans le conteneur, avec un budget de 128 Mio par recherche et un plafond de 32. Il est recalcule avant chaque lot, respecte les limites CPU/memoire du worker et conserve les garde-fous C411. Il s'agit d'une limite de capacite prudente, pas d'une mesure du maximum absolu ni d'un ajustement continu a la charge de Plex. Le mode manuel accepte de 1 a 32 recherches. Augmenter le parallelisme absorbe la latence mais ne contourne pas l'espacement de deux secondes entre departs. Le journal affiche la limite effective au demarrage du lot.

Le bouton **Lancer un scan** demande un passage immediat sur les medias surveilles, sans attendre leur prochaine echeance. La veille globale doit etre active. Un scan deja en cours est reutilise : les clics repetes ne creent pas de recherches concurrentes supplementaires. Les exclusions, les pauses individuelles, la reference initiale et les blacklists sont preservees. Une suspension C411 ne peut pas etre contournee par ce bouton.

La page relit automatiquement la progression toutes les deux secondes, sans requetes de polling superposees. Elle affiche la file, les medias en cours, les succes, erreurs et medias ignores, ainsi que les nouvelles propositions. Le journal pagine distingue l'attente du creneau C411, l'attente de la reponse et le traitement des resultats ; chaque media comporte une duree et les compteurs du passage (releases recues, correspondances, rejets du profil). Les erreurs sont assainies pour ne pas exposer de secrets ni de liens de telechargement.

Le minimum restant ne comprend ni la latence reseau, ni les pauses entre lots, ni les quotas : ce n'est pas une heure de fin garantie. Chaque recherche est limitee a deux minutes. Les lots de 100 sont separes par le passage du planificateur toutes les 15 secondes ; une limite C411 affiche sa raison et la date de reprise au plus tot. L'absence du heartbeat worker pendant plus de 60 secondes est signalee separement. Les scans et leurs details sont persistants ; les recherches interrompues reprennent au redemarrage du worker, sans nouvelle instance parallele. Les 20 derniers scans termines sont conserves en base ; la page affiche le scan actif ou le dernier termine.

## Torrents Enregistres

Chaque scan reussi conserve en base la liste des torrents du media, avec leurs metadonnees et la date de verification. Une liste vide remplace les anciens resultats ; une erreur conserve le dernier instantane reussi. L'ouverture d'une fiche et la selection d'un episode ne contactent pas C411. Le bouton **Rechercher sur C411** lance une recherche manuelle et remplace l'instantane du media si elle reussit, sans creer de job ni de notification. Les resultats persistent apres un redemarrage. Les anciennes observations sont affichees en transition jusqu'au prochain scan, avec des metadonnees partielles. Les films exclus de la veille peuvent etre actualises manuellement.

## Details Du Stock

Chaque fiche donne acces a une page **Details du stock**, avec l'inventaire, les dates d'analyse, la taille totale et la duree connue, les caracteristiques video, toutes les pistes audio/sous-titres et les sous-titres externes associes par nom. Les valeurs absentes restent non renseignees ; le debit global et les debits video/audio sont separes. Les debits proviennent des metadonnees du fichier, pas d'une mesure exhaustive du flux. La numerotation saison/episode affichee est celle du nom de fichier.

Le GET `/api/media/{id}/stock-details` lit exclusivement PostgreSQL : ouvrir cette page ne sonde pas RaiDrive. Une demande POST met l'analyse en file persistante dans l'unique worker serveur, un fichier a la fois. Le bouton **Analyser un fichier** borne la demande a un fichier nouveau/modifie ; **Analyser les fichiers nouveaux ou modifies** traite les fichiers sans resultat valide, compares par chemin/taille/date de modification. L'icone de chaque fichier force sa reanalyse. Une modification conservant exactement taille et date ne peut pas etre detectee sans lecture supplementaire ; utiliser alors la reanalyse explicite.

Le sondage ffprobe a une limite de 30 secondes par fichier, un probesize de 5 Mo et une analyzeduration de 5 secondes. Ces reglages limitent le sondage, mais ne constituent pas une limite des octets telecharges par RaiDrive : les recherches dans le conteneur et le cache du montage peuvent generer d'autres lectures. Les analyses n'ont aucun effet sur les fichiers ni sur les jobs de download/encodage. Aucune analyse globale ne demarre automatiquement.

Les resultats sont enregistres fichier par fichier et repris apres redemarrage. Les echecs partiels n'effacent pas les resultats precedents ; une panne du montage ne transforme pas le media en Hors stock. Annuler conserve les resultats acquis et empeche le traitement de fichiers suivants ; le sondage courant peut finir jusqu'a son delai maximal, mais son resultat n'est pas applique apres annulation. Un inventaire reussi signale les fichiers absents sans effacer leur historique.

## Decisions

- Chaque fiche dispose d'un interrupteur individuel persistant. Le desactiver suspend les recherches et les notifications de ce media et expire ses propositions en attente, sans annuler les jobs existants. La reference et les blacklists sont conservees pour une reactivation. Une pause globale reste prioritaire ; l'interrupteur ne peut pas contourner l'exclusion des films deja en stock.
- Aucun download n'est cree par un scan ou une notification.
- La veille et la fiche utilisent le meme profil de qualite (profil du media ou profil par defaut). **Filtre** signifie uniquement que la release ne passe pas ces filtres. Une nouvelle release valide est proposee meme si la recherche TMDB ne fournit pas son lien de telechargement.
- Accepter reevalue la disponibilite, l'identite du media et le profil actuel. Si le lien manque, une recherche Torznab ciblee le resout pour la meme empreinte (titre et taille), puis cree un seul job Download dans une transaction PostgreSQL. Un lien introuvable conserve la proposition en attente sans creer de job. Les anciennes releases classees a tort comme filtrees sont reevaluees au prochain scan, sans modifier la reference initiale ni les blacklists.
- Blacklister persiste le refus pour cette release et ce media, meme apres un redemarrage. Une release distincte peut encore etre proposee.
- Les propositions expirent apres 72 heures. La page Veille conserve les decisions et permet aussi d'accepter ou de blacklister.
- Les releases sont dedupliquees par titre normalise et taille, independamment des tokens de telechargement. Deux releases de titre et taille identiques sont volontairement considerees equivalentes.

## Selection Des Pistes Sur Telegram

Les jobs **WaitingForSelection** recoivent une demande Telegram pour choisir plusieurs pistes audio et sous-titres. Les recommandations du site sont preselectionnees ; chaque bouton coche ou decoche une piste. Les pistes d'audiodescription et les sous-titres SDH restent exclus. Les listes sont paginees par huit pistes et les choix sont conserves en base pendant la navigation et apres un redemarrage.

**Confirmer** valide le fichier courant. **Confirmer pour les pistes identiques** utilise exactement le meme service que le bouton du site : meme media, comparaison complete des caracteristiques audio/sous-titres, et creation d'un job Encode par fichier compatible. La validation exige au moins une piste audio et verifie que les pistes existent encore. Un double clic ou une confirmation simultanee depuis le site ne cree pas deux encodages.

Une seule demande de pistes est active a la fois dans le chat configure. Apres validation groupee, le bot relit les jobs en attente : les episodes deja valides ne recoivent aucune autre demande. Les boutons d'une demande terminee ou annulee depuis le site sont retires au prochain passage du bot (environ quinze secondes), et les anciens callbacks sont refuses immediatement. Les nouveaux boutons respectent la meme restriction au compte et au chat prives que les decisions de releases. Aucun job ne reprend avant confirmation explicite. Comme pour les notifications de releases, une panne entre l'envoi Telegram et l'enregistrement de son identifiant peut exceptionnellement dupliquer un message ; ses confirmations restent idempotentes.

## Configuration Telegram Sur Le Serveur

1. Creer un bot dedie a MediaHub avec **@BotFather**, dans Telegram.
2. Ouvrir un terminal SSH sur le serveur :

```bash
cd ~/Websites/MediaHub
python3 scripts/configure-telegram.py
```

3. Entrer le token dans l'invite masquee du terminal, jamais dans le chat Copilot ni dans Git. Envoyer `/start` au bot depuis le telephone quand le script le demande.
4. Selectionner son propre identifiant de chat prive, confirmer la reception du message test, puis laisser le script relancer API, frontend et worker.

Le token et l'identifiant sont conserves dans `.env` (mode 600, ignore par Git). Le script n'affiche pas le token. Le worker utilise `getUpdates` en polling sortant : aucun webhook, port entrant, domaine public ou lien de telechargement n'est necessaire. Seul l'utilisateur configure, dans son chat prive, peut decider. Ne pas reutiliser ce bot dans un autre service.

Sans Telegram configure, les propositions sont conservees et restent visibles dans Veille ; aucune notification mobile n'est envoyee. Le bouton **Tester Telegram** permet ensuite de verifier l'envoi.

## Fiabilite Et Limites

Une seule instance de worker est supportee, comme pour la pipeline existante. Les notifications ont une file persistante avec reprise exponentielle, mais un crash entre l'envoi Telegram et sa confirmation en base peut produire un message duplique. Les decisions restent idempotentes et ne creent pas de second job. Aucun secret ni URL de telechargement n'est transmis dans les notifications.

Le monitoring des incidents et les alertes DOWN/RECOVERED restent des evolutions distinctes. L'authentification applicative multiutilisateur reste a ajouter avant une exposition publique aux amis.

## Tests

Les tests transactionnels utilisent des bases PostgreSQL temporaires au nom `mediahub_watch_tests_<uuid>`, creees puis supprimees. Renseigner `MEDIAHUB_TEST_DB` avec une connexion autorisee a creer des bases ; sans cette variable, seuls ces tests d'integration sont ignores. La CI fournit PostgreSQL et les execute.